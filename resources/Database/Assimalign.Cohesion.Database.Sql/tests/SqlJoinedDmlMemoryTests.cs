using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlJoinedDmlMemoryTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: 50,000 nonmatching source rows obey the statement grant (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id>s.id WHEN NOT MATCHED AND FALSE THEN INSERT VALUES(s.id,s.v)")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id>s.id")]
    [InlineData("DELETE FROM t USING s WHERE t.id>s.id")]
    public async Task ExecuteAsync_NonmatchingSourceExceedsBudget_ShouldRefuseBeforeWriting(string sql)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var engine = SqlDatabaseEngine.Create("joined-memory-probe", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var other = await database.CreateSessionAsync(guard.Token);
        await SetupAsync(session, guard.Token);
        for (int start = 2; start <= 50_001; start += 1000)
        {
            await session.ExecuteAsync("INSERT INTO s VALUES" + string.Join(',', Enumerable.Range(start, 1000).Select(id => $"({id},{id})")),
                cancellationToken: guard.Token);
        }
        await session.ExecuteAsync("BEGIN", cancellationToken: guard.Token);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: guard.Token));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        await other.ExecuteAsync("DROP TABLE s", cancellationToken: guard.Token);
        await session.ExecuteAsync("COMMIT", cancellationToken: guard.Token);

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 1024);
        metrics.RecordsExamined.ShouldBeLessThan(50_001);
        (await RowsAsync(session, "SELECT id,v FROM t", guard.Token)).ShouldHaveSingleItem().ShouldBe([1, 1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: a small statement succeeds and releases the shared grant (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=s.v", 7)]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=s.id", 7)]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id", null)]
    [InlineData("MERGE INTO t USING s ON t.id>=s.id WHEN MATCHED THEN UPDATE SET v=s.v", 7)]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id>=s.id", 7)]
    [InlineData("DELETE FROM t USING s WHERE t.id>=s.id", null)]
    public async Task ExecuteAsync_UnderBudget_ShouldSucceedAndReleaseMemory(string sql, int? expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("joined-memory-small", new() { StatementMemoryBudget = 65_536 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES(1,7)", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        result.AffectedCount.ShouldBe(1);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 65_536);
        var stored = await RowsAsync(session, "SELECT v FROM t", CancellationToken.None);
        if (expected is null) { stored.ShouldBeEmpty(); }
        else { stored.ShouldHaveSingleItem().ShouldBe([expected.Value]); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: decoded target identities obey the grant even with no source rows (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id>s.id WHEN MATCHED THEN DELETE")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id>s.id")]
    [InlineData("DELETE FROM t USING s WHERE t.id>s.id")]
    public async Task ExecuteAsync_TargetsExceedBudget_ShouldRefuseWithoutWriting(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("joined-memory-target", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(2, 99).Select(id => $"({id},{id})")),
            cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 1024);
        metrics.RecordsExamined.ShouldBeLessThan(100);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t", CancellationToken.None))[0][0].ShouldBe(100L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: selected write payloads obey the grant before apply (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=large_value(s.v)")]
    [InlineData("UPDATE t SET v=large_value(s.v) FROM s WHERE t.id=s.id")]
    [InlineData("MERGE INTO t USING s ON t.id>s.id WHEN NOT MATCHED THEN INSERT VALUES(s.id+1,large_value(s.v))")]
    public async Task ExecuteAsync_WriteExceedsBudget_ShouldRefuseBeforeWriting(string sql)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("joined-memory-write");
        builder.Options.StatementMemoryBudget = 8192;
        builder.Functions.Add(SqlScalarFunction.Create("large_value", (int _) => { calls++; return new string('x', 12_000); }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in new[] { "CREATE TABLE t(id INT,v TEXT)", "CREATE TABLE s(id INT,v INT)",
            "INSERT INTO t VALUES(1,'old')", "INSERT INTO s VALUES(1,1)" })
        { await session.ExecuteAsync(setup, cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        calls.ShouldBe(1);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 8192);
        (await RowsAsync(session, "SELECT v FROM t", CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(["old"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: constraint failures release retained memory and locks (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=s.v")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id=s.id")]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id")]
    public async Task ExecuteAsync_ConstraintFailure_ShouldReleaseMemoryAndLocks(string sql)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("joined-memory-constraint", new() { StatementMemoryBudget = 65_536 });
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var other = await database.CreateSessionAsync(guard.Token);
        foreach (string setup in new[] { "CREATE TABLE t(id INT PRIMARY KEY,v INT CHECK(v<10))", "CREATE TABLE s(id INT,v INT)",
            "CREATE TABLE c(pid INT REFERENCES t(id))", "INSERT INTO t VALUES(1,1)", "INSERT INTO s VALUES(1,11)",
            "INSERT INTO c VALUES(1)", "BEGIN" })
        { await session.ExecuteAsync(setup, cancellationToken: guard.Token); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: guard.Token));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        await other.ExecuteAsync("DROP TABLE s", cancellationToken: guard.Token);
        await session.ExecuteAsync("COMMIT", cancellationToken: guard.Token);

        // Assert
        failure.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Code == "CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 65_536);
        (await RowsAsync(session, "SELECT id,v FROM t", guard.Token)).ShouldHaveSingleItem().ShouldBe([1, 1]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: cancellation releases retained memory and locks (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=cancel_value(s.v)")]
    [InlineData("UPDATE t SET v=cancel_value(s.v) FROM s WHERE t.id=s.id")]
    [InlineData("DELETE FROM t USING s WHERE t.id=s.id AND cancel_value(s.v)=s.v")]
    public async Task ExecuteAsync_Cancellation_ShouldReleaseMemoryAndLocks(string sql)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("joined-memory-cancel");
        builder.Functions.Add(SqlScalarFunction.Create("cancel_value", (int value) => { calls++; cancellation.Cancel(); return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var other = await database.CreateSessionAsync(guard.Token);
        await SetupAsync(session, guard.Token);
        await session.ExecuteAsync("INSERT INTO s VALUES(1,7)", cancellationToken: guard.Token);
        await session.ExecuteAsync("BEGIN", cancellationToken: guard.Token);

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(sql, cancellationToken: cancellation.Token));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        await other.ExecuteAsync("DROP TABLE s", cancellationToken: guard.Token);
        await session.ExecuteAsync("COMMIT", cancellationToken: guard.Token);

        // Assert
        calls.ShouldBeGreaterThan(0);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
        (await RowsAsync(session, "SELECT id,v FROM t", guard.Token)).ShouldHaveSingleItem().ShouldBe([1, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: hash ownership covers aliased rows without a second payload charge (#1715, #1751)")]
    public void Retain_SharedHashPayload_ShouldChargeOnceAndTransferOwnership()
    {
        // Arrange
        var metrics = new SqlStatementMetrics();
        using var memory = new SqlDmlMemory(metrics, 65_536);
        object?[] row = [new string('x', 100), new byte[100]];
        memory.Retain(row);
        long retained = metrics.HashMemoryBytes;
        long hashBytes = SqlHashJoinTable.RowBytes(row);

        // Act / Assert: registering the same object twice adds no reservation.
        memory.Retain(row);
        metrics.HashMemoryBytes.ShouldBe(retained);
        memory.HashRow(row, adding: true);
        metrics.ReserveHashMemory(hashBytes, 65_536);
        metrics.HashMemoryBytes.ShouldBe(retained);
        metrics.ReleaseHashMemory(hashBytes);
        memory.HashRow(row, adding: false);
        metrics.HashMemoryBytes.ShouldBe(retained);
        memory.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: buffered source growth is refused after target admission (#1715, #1751)")]
    [InlineData("MERGE INTO t USING s ON t.id>s.id WHEN NOT MATCHED AND FALSE THEN INSERT VALUES(s.id,s.v)")]
    [InlineData("UPDATE t SET v=s.v FROM s WHERE t.id>s.id")]
    [InlineData("DELETE FROM t USING s WHERE t.id>s.id")]
    [InlineData("MERGE INTO t USING (SELECT id,v FROM s) AS q ON t.id>q.id WHEN NOT MATCHED AND FALSE THEN INSERT VALUES(q.id,q.v)")]
    [InlineData("UPDATE t SET v=q.v FROM (SELECT id,v FROM s) AS q WHERE t.id>q.id")]
    [InlineData("DELETE FROM t USING (SELECT id,v FROM s) AS q WHERE t.id>q.id")]
    public async Task ExecuteAsync_BufferedSourceExceedsBudget_ShouldRefuseBeforeWriting(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("joined-memory-input", new() { StatementMemoryBudget = 8192 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES" + string.Join(',', Enumerable.Range(2, 200).Select(id => $"({id},{id})")),
            cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.RecordsExamined.ShouldBeGreaterThan(1);
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 8192);
        (await RowsAsync(session, "SELECT id,v FROM t", CancellationToken.None)).ShouldHaveSingleItem().ShouldBe([1, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: released hash-only slots do not accumulate payload or capacity charges (#1715, #1751)")]
    public void HashRow_RepeatedUnselectedBuilds_ShouldReuseAccountedSlots()
    {
        // Arrange
        var metrics = new SqlStatementMetrics();
        using var memory = new SqlDmlMemory(metrics, 4096);
        long? remaining = null;

        // Act / Assert
        for (int i = 0; i < 100; i++)
        {
            object?[] row = [new string('x', 100), new byte[100]];
            memory.HashRow(row, adding: true);
            long hashBytes = SqlHashJoinTable.RowBytes(row);
            metrics.ReserveHashMemory(hashBytes, 4096);
            metrics.ReleaseHashMemory(hashBytes);
            memory.HashRow(row, adding: false);
            remaining ??= metrics.HashMemoryBytes;
            metrics.HashMemoryBytes.ShouldBe(remaining.Value);
        }
        memory.Dispose();
        metrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: referential write collection obeys the statement grant (#1715, #1751)")]
    [InlineData("CASCADE", "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE")]
    [InlineData("SET NULL", "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN DELETE")]
    [InlineData("CASCADE", "DELETE FROM t USING s WHERE t.id=s.id")]
    [InlineData("SET NULL", "DELETE FROM t USING s WHERE t.id=s.id")]
    public async Task ExecuteAsync_ReferentialWritesExceedBudget_ShouldRefuseBeforeWriting(string action, string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("joined-memory-referential", new() { StatementMemoryBudget = 8192 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string setup in new[] { "CREATE TABLE t(id INT PRIMARY KEY,v INT)", "CREATE TABLE s(id INT,v INT)",
            $"CREATE TABLE c(id INT,pid INT REFERENCES t(id) ON DELETE {action})", "INSERT INTO t VALUES(1,1)", "INSERT INTO s VALUES(1,1)",
            "INSERT INTO c VALUES" + string.Join(',', Enumerable.Range(1, 100).Select(id => $"({id},1)")) })
        { await session.ExecuteAsync(setup, cancellationToken: CancellationToken.None); }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 8192);
        (await RowsAsync(session, "SELECT id,v FROM t", CancellationToken.None)).ShouldHaveSingleItem().ShouldBe([1, 1]);
        (await RowsAsync(session, "SELECT COUNT(*) FROM c WHERE pid=1", CancellationToken.None))[0][0].ShouldBe(100L);
    }

    private static async Task SetupAsync(SqlDatabaseSession session, CancellationToken cancellationToken)
    {
        foreach (string sql in new[] { "CREATE TABLE t(id INT,v INT)", "CREATE TABLE s(id INT,v INT)", "INSERT INTO t VALUES(1,1)" })
        { await session.ExecuteAsync(sql, cancellationToken: cancellationToken); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: cancellationToken);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(cancellationToken))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++) { values[i] = row.GetValue(i); }
            rows.Add(values);
        }
        return rows;
    }
}
