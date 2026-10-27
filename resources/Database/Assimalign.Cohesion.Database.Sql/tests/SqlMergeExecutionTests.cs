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

public sealed class SqlMergeExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: ordered actions, both unmatched directions and RETURNING execute atomically (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_AllActions_ShouldReturnChangedRowsAsync(bool transaction)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-actions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10),(2,20),(3,30),(4,40),(5,50)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11),(2,22),(3,33),(6,66),(7,77)");
        if (transaction) { await ExecuteAsync(session, "BEGIN"); }

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING s ON t.id=s.id " +
            "WHEN MATCHED AND s.id=1 THEN UPDATE SET v=s.v " +
            "WHEN MATCHED AND s.id=2 THEN DELETE WHEN MATCHED THEN DO NOTHING " +
            "WHEN NOT MATCHED BY TARGET AND s.id=6 THEN INSERT VALUES(s.id,s.v) " +
            "WHEN NOT MATCHED THEN DO NOTHING " +
            "WHEN NOT MATCHED BY SOURCE AND t.id=4 THEN UPDATE SET v=t.v+1 " +
            "WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING t.id,t.v,s.id,merge_action()", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(result);
        if (transaction) { await ExecuteAsync(session, "COMMIT"); }

        // Assert
        result.AffectedCount.ShouldBe(5);
        rows.OrderBy(row => row[0]).Select(row => string.Join(",", row)).ShouldBe([
            "1,11,1,UPDATE", "2,20,2,DELETE", "4,41,,UPDATE", "5,50,,DELETE", "6,66,6,INSERT"]);
        var stored = await session.ExecuteAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: CancellationToken.None);
        (await RowsAsync(stored)).Select(row => string.Join(",", row)).ShouldBe(["1,11", "3,30", "4,41", "6,66"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: repeated modifying targets use CS05036 and preserve prior statements (#1715)")]
    [InlineData("UPDATE SET v=s.v")]
    [InlineData("DELETE")]
    public async Task Execute_RepeatedTarget_ShouldFailWithoutWritesAsync(string action)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-repeat", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11),(1,12)");
        await ExecuteAsync(session, "BEGIN");
        await ExecuteAsync(session, "INSERT INTO t VALUES(9,90)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN " + action));
        await ExecuteAsync(session, "COMMIT");

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05036");
        var stored = await session.ExecuteAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: CancellationToken.None);
        (await RowsAsync(stored)).Select(row => string.Join(",", row)).ShouldBe(["1,10", "9,90"]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: duplicate matches with DO NOTHING do not count as modifications (#1715)")]
    public async Task Execute_DuplicateNoOps_ShouldModifyOnlyOnceAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-noops", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11),(1,12)");

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING s ON t.id=s.id " +
            "WHEN MATCHED AND s.v=11 THEN DO NOTHING WHEN MATCHED THEN UPDATE SET v=s.v RETURNING t.v,merge_action()", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(result)).ShouldHaveSingleItem().ShouldBe([12, "UPDATE"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: late uniqueness and RETURNING failures roll back all actions (#1715)")]
    [InlineData("WHEN MATCHED THEN DELETE WHEN NOT MATCHED THEN INSERT VALUES(8,s.v)", "t.id", "CS03037")]
    [InlineData("WHEN MATCHED THEN UPDATE SET v=s.v WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v)", "10/(t.id-3)", "CS02001")]
    public async Task Execute_ApplyFailure_ShouldRollbackAllActionsAsync(string actions, string returning, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-rollback", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11),(2,22),(3,33)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session,
            "MERGE INTO t USING s ON t.id=s.id " + actions + " RETURNING " + returning));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        var stored = await session.ExecuteAsync("SELECT id,v FROM t", cancellationToken: CancellationToken.None);
        (await RowsAsync(stored)).ShouldHaveSingleItem().ShouldBe([1, 10]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: action subqueries and scalar branches are lazy (#1715)")]
    public async Task Execute_ActionQueries_ShouldEvaluateOnlyReachedExpressionsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-lazy", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10),(2,20)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11),(2,22),(3,33)");

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING s ON t.id=s.id " +
            "WHEN MATCHED AND s.id=1 THEN DO NOTHING " +
            "WHEN MATCHED AND CASE WHEN s.id=2 THEN TRUE ELSE EXISTS(SELECT 1/0) END THEN UPDATE SET v=(SELECT s.v) " +
            "WHEN MATCHED AND EXISTS(SELECT 1/0) THEN UPDATE SET v=(SELECT 1/0) " +
            "WHEN NOT MATCHED THEN INSERT VALUES(s.id,(SELECT s.v)) RETURNING t.id,t.v", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(2);
        (await RowsAsync(result)).OrderBy(row => row[0]).Select(row => string.Join(",", row)).ShouldBe(["2,22", "3,33"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: source presence is retained for all-NULL rows and normalized joins (#1715)")]
    [InlineData("(SELECT NULL AS id,NULL AS v) AS s", "TRUE", "WHEN MATCHED THEN DELETE", 1)]
    [InlineData("(SELECT NULL AS id,NULL AS v) AS s", "FALSE", "WHEN NOT MATCHED THEN INSERT VALUES(2,20)", 1)]
    [InlineData("s", "t.id>s.id", "WHEN NOT MATCHED BY SOURCE THEN DELETE", 1)]
    [InlineData("s", "t.id>s.id", "WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v)", 1)]
    public async Task Execute_NormalizedPresence_ShouldClassifyCandidatesAsync(string source, string on, string actions, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-presence", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(2,20)");

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING " + source + " ON " + on + " " + actions, cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(count);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: INSERT defaults, identity and simultaneous assignments share DML rules (#1715)")]
    public async Task Execute_DefaultsAndIdentity_ShouldFollowInsertRulesAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-defaults", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,v INT DEFAULT 7,w INT DEFAULT 9)");
        await ExecuteAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id UNION ALL SELECT 2) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN UPDATE SET v=t.w,w=t.v WHEN NOT MATCHED THEN INSERT DEFAULT VALUES RETURNING t.id,t.v,t.w,merge_action()", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(2);
        (await RowsAsync(result)).OrderBy(row => row[0]).Select(row => string.Join(",", row)).ShouldBe(["1,9,7,UPDATE", "2,7,9,INSERT"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: DELETE reuses referential actions (#1715)")]
    [InlineData("CASCADE", 0)]
    [InlineData("SET NULL", 1)]
    public async Task Execute_ParentDelete_ShouldApplyReferentialActionsAsync(string policy, int children)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("merge-references", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "CREATE TABLE child(id INT,pid INT REFERENCES t(id) ON DELETE " + policy + ")");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO child VALUES(1,1)");

        // Act
        var result = await session.ExecuteAsync("MERGE INTO t USING s ON t.id=s.id WHEN NOT MATCHED BY SOURCE THEN DELETE RETURNING merge_action()", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(result)).ShouldHaveSingleItem().ShouldBe(["DELETE"]);
        var stored = await session.ExecuteAsync("SELECT pid FROM child", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(stored);
        rows.Count.ShouldBe(children);
        if (children > 0) { rows[0][0].ShouldBeNull(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: hash budget failure releases memory and statement locks (#1715)")]
    public async Task Execute_HashBudgetFailure_ShouldReleaseStatementResourcesAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var other = await database.CreateSessionAsync(timeout.Token);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES" + string.Join(',', Enumerable.Range(2, 80).Select(id => $"({id},{id})")));
        await ExecuteAsync(session, "BEGIN");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING s ON t.id=s.id WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v) WHEN NOT MATCHED BY SOURCE THEN DELETE", cancellationToken: timeout.Token));
        var metrics = session.LastStatementMetrics!;
        await other.ExecuteAsync("DROP TABLE s", cancellationToken: timeout.Token);
        await ExecuteAsync(session, "COMMIT");

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05067");
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 1024);
        var stored = await session.ExecuteAsync("SELECT id,v FROM t", cancellationToken: timeout.Token);
        (await RowsAsync(stored)).ShouldHaveSingleItem().ShouldBe([1, 10]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: cancellation during action evaluation releases statement locks (#1715)")]
    public async Task Execute_ActionCancellation_ShouldKeepTransactionUsableAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("merge-cancel");
        builder.Functions.Add(SqlScalarFunction.Create("merge_cancel", (int value) =>
        {
            calls++;
            cancellation.Cancel();
            return value;
        }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await using var other = await database.CreateSessionAsync(guard.Token);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11)");
        await ExecuteAsync(session, "BEGIN");

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=merge_cancel(s.v)", cancellationToken: cancellation.Token));
        await other.ExecuteAsync("DROP TABLE s", cancellationToken: guard.Token);
        await ExecuteAsync(session, "COMMIT");

        // Assert
        calls.ShouldBe(1);
        var stored = await session.ExecuteAsync("SELECT id,v FROM t", cancellationToken: guard.Token);
        (await RowsAsync(stored)).ShouldHaveSingleItem().ShouldBe([1, 10]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: binding and skipped action subqueries never invoke immutable callbacks (#1715)")]
    public async Task Execute_SkippedImmutableSubqueries_ShouldNotInvokeCallbacksAsync()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("merge-bind-lazy");
        builder.Functions.Add(SqlScalarFunction.Create("merge_probe", (int value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SetupAsync(session);
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10)");
        await ExecuteAsync(session, "INSERT INTO s VALUES(1,11)");
        const string sql = "MERGE INTO t USING s ON t.id=s.id WHEN MATCHED AND TRUE THEN DO NOTHING " +
            "WHEN MATCHED AND EXISTS(SELECT merge_probe(1) FROM t WHERE id=merge_probe(1)) " +
            "THEN UPDATE SET v=(SELECT merge_probe(1) LIMIT merge_probe(1))";

        // Act
        var explanation = await session.ExecuteAsync("EXPLAIN " + sql, cancellationToken: CancellationToken.None);
        await RowsAsync(explanation);
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        calls.ShouldBe(0);
        result.AffectedCount.ShouldBe(0);
    }

    private static async Task SetupAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,v INT)");
        await ExecuteAsync(session, "CREATE TABLE s(id INT,v INT)");
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(DatabaseResultStatus.Success);
    }

    private static async Task<List<object?[]>> RowsAsync(DatabaseResult result)
    {
        var rows = new List<object?[]>();
        await using var set = result.ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in set.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
