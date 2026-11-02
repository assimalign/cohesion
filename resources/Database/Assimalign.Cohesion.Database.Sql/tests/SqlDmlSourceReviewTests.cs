using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlDmlSeekCollection.Name)]
public sealed class SqlDmlSourceReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlDmlSourceReviewTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: overview claims match executable profile (#1714)")]
    public void Documentation_JoinedDmlOverviews_ShouldDescribeExecution()
    {
        // Arrange
        string root = DatabaseDirectory();

        // Act / Assert
        foreach (string project in new[] { "Assimalign.Cohesion.Database.Sql", "Assimalign.Cohesion.Database.Sql.Language" })
        {
            string overview = File.ReadAllText(Path.Combine(root, project, "docs", "OVERVIEW.md"));
            overview.ShouldContain("UPDATE FROM and DELETE USING execute", Case.Sensitive);
            overview.ShouldNotContain("execution refused by CK00001", Case.Sensitive);
            overview.ShouldNotContain("absent from the executable clause profile", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: cardinality registry row includes its category (#1714)")]
    public void Documentation_CardinalityRegistry_ShouldUseExecutionCategory()
    {
        // Arrange
        string registry = Path.Combine(DatabaseDirectory(), "Assimalign.Cohesion.Database", "docs", "ERROR-CODES.md");

        // Act
        string row = File.ReadLines(registry).Single(line => line.Contains("| CS05068 |", StringComparison.Ordinal));

        // Assert
        row.Split('|')[3].Trim().ShouldStartWith("Execution/budgets — ", Case.Sensitive);
    }

    private static string DatabaseDirectory([CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

#if !DEBUG
    [Theory(DisplayName = "Cohesion Test [Sql] - Plain DML: joined association state adds no allocation (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_PlainDml_ShouldKeepBaseAllocationBudget(bool delete)
    {
        // Arrange: an empty seek keeps the measurement independent of version retention.
        await using var engine = SqlDatabaseEngine.Create("dml-plain-allocation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var request = SqlQueryRequest.FromSql(delete ? "DELETE FROM t WHERE id = 1" : "UPDATE t SET v = 1 WHERE id = 1");
        Measure(1000);

        // Act
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++) { allocated = Math.Min(allocated, Measure(1000)); }
        _output.WriteLine($"plain {(delete ? "DELETE" : "UPDATE")} {RuntimeInformation.ProcessArchitecture} bytes/1000={allocated}");

        // Assert: e33bb46c1, Release .NET 10.0.13, win-arm64 and --arch x64 both measured
        // UPDATE 11,408,000 and DELETE 10,832,000 bytes/1000 on this full session path.
        // Allow 4,000 bytes of headroom, below one extra captured reference (8,000/1000).
        allocated.ShouldBeLessThanOrEqualTo(delete ? 10_836_000 : 11_412_000);

        long Measure(int count)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++)
            {
                var pending = session.ExecuteAsync(request, CancellationToken.None);
                if (!pending.IsCompletedSuccessfully) { throw new InvalidOperationException("Allocation probe suspended."); }
                _ = pending.Result;
            }
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
#endif

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: equality visits linear inputs and uses a budgeted hash (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_Equijoin_ShouldBoundCandidateWork(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-work", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "s" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT,v INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1,100).Select(i => $"({i},{i})")), cancellationToken: CancellationToken.None);
        }
        string sql = delete ? "DELETE FROM t USING s WHERE t.id=s.id" : "UPDATE t SET v=s.v FROM s WHERE t.id=s.id";

        // Act
        var explain = await RowsAsync(session, "EXPLAIN " + sql);
        (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(100);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        _output.WriteLine($"records={metrics.RecordsExamined}; association_reads={metrics.DmlAssociationRowReads}; total_reads={metrics.RowReads}; pairs={metrics.JoinCandidatePairs}; peak_hash={metrics.PeakHashMemoryBytes}");

        // Assert
        metrics.RecordsExamined.ShouldBe(200);
        metrics.DmlAssociationRowReads.ShouldBe(200);
        metrics.PeakDmlTargetRows.ShouldBe(100);
        metrics.RowReads.ShouldBeGreaterThan(metrics.DmlAssociationRowReads);
        metrics.JoinCandidatePairs.ShouldBe(100);
        metrics.JoinAccessPaths.ShouldBe(["join-hash"]);
        metrics.PeakHashMemoryBytes.ShouldBeGreaterThan(0);
        metrics.HashMemoryBytes.ShouldBe(0);
        explain.Any(row => row.OfType<string>().Any(text => text.Contains("HashJoin", StringComparison.Ordinal))).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: unindexed target filter retains only qualifying identities (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TargetFilter_ShouldRetainOneIdentity(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-target-filter", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,flag INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(',', Enumerable.Range(1, 1000)
            .Select(i => $"({i},{(i == 501 ? 1 : 0)},0)")), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES(501,7)", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(delete ? "DELETE FROM t USING s WHERE t.flag=1 AND t.id=s.id"
            : "UPDATE t SET v=s.v FROM s WHERE t.flag=1 AND t.id=s.id", cancellationToken: CancellationToken.None);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        _output.WriteLine($"records={metrics.RecordsExamined}; association_reads={metrics.DmlAssociationRowReads}; retained={metrics.PeakDmlTargetRows}; total_reads={metrics.RowReads}");

        // Assert
        result.AffectedCount.ShouldBe(1);
        metrics.PeakDmlTargetRows.ShouldBe(1);
        metrics.RecordsExamined.ShouldBe(1001);
        metrics.DmlAssociationRowReads.ShouldBe(1001);
        metrics.JoinCandidatePairs.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: exact source search and fixed target boundary are documented (#1629)")]
    public void Documentation_SourceOrder_ShouldDescribeCurrentExecution()
    {
        // Arrange
        string root = DatabaseDirectory();
        string[] documents = [Path.Combine(root, "Assimalign.Cohesion.Database.Sql", "docs", "DESIGN.md"),
            Path.Combine(root, "Assimalign.Cohesion.Database.Sql.Language", "docs", "DIALECT.md")];

        // Act / Assert
        foreach (string path in documents)
        {
            string text = File.ReadAllText(path);
            text.ShouldContain("#1629", Case.Sensitive);
            text.ShouldContain("eight", Case.Sensitive);
            text.ShouldContain("target/source boundary", Case.Sensitive);
            text.ShouldContain("SQ-Z2", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Joined DML: retry comment names all preparation catch sites (#1714)")]
    public void Documentation_CascadeRetry_ShouldNameAllCatchSites()
    {
        // Arrange
        string path = Path.Combine(DatabaseDirectory(), "Assimalign.Cohesion.Database.Sql", "src", "Internal", "Execution", "SqlPlanExecutor.CascadeRetry.cs");

        // Act
        string retries = File.ReadAllText(path);

        // Assert
        retries.ShouldContain("ExecutePlainDeleteAsync", Case.Sensitive);
        retries.ShouldContain("PrepareJoinedUpdateIntentsAsync", Case.Sensitive);
        retries.ShouldContain("ExecuteJoinedDeleteAsync", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: a primary-key target restriction seeks once with target outer (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_TargetPrimaryKey_ShouldSeekOneTarget(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-target-seek", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE s(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        foreach (string table in new[] { "t", "s" })
        { await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1,100).Select(i => $"({i},{i})")), cancellationToken: CancellationToken.None); }
        string sql = delete ? "DELETE FROM t USING s WHERE t.id=5 AND s.id=5" : "UPDATE t SET v=s.v FROM s WHERE t.id=5 AND s.id=5";

        // Act
        var explain = await RowsAsync(session, "EXPLAIN " + sql);
        (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(1);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();
        _output.WriteLine($"records={metrics.RecordsExamined}; pairs={metrics.JoinCandidatePairs}");

        // Assert: one target fetch and one source fetch, no target scan.
        metrics.RecordsExamined.ShouldBe(2);
        metrics.DmlAssociationRowReads.ShouldBe(2);
        metrics.PeakDmlTargetRows.ShouldBe(1);
        metrics.JoinCandidatePairs.ShouldBe(1);
        explain.Count(row => row.Contains("IndexSeek")).ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: costed CTE identity materializes a volatile source once (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolatileCte_ShouldReuseMaterialization(bool delete)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-cte-once");
        builder.Functions.Add(SqlScalarFunction.Create("source_probe", (long value) => 100 * ++calls + value, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t(id INT,v BIGINT)", "CREATE TABLE s(id INT)", "INSERT INTO t VALUES(1,0)", "INSERT INTO s VALUES(1)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }

        // Act
        var rows = await RowsAsync(session, "WITH q AS (SELECT source_probe(id) AS v FROM s) " + (delete
            ? "DELETE FROM t USING q RETURNING t.v,q.v" : "UPDATE t SET v=q.v FROM q RETURNING t.v,q.v"));

        // Assert
        rows.ShouldBe(new object?[][] { [delete ? 0L : 101L,101L] });
        calls.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: wrapper rejection releases new source locks and preserves prior grants (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CteCardinalityFailure_ShouldReleaseBeforeTransactionEnds(bool subquery)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("dml-cte-unwind", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in new[] { "CREATE TABLE prior(id INT)", "CREATE TABLE t(id INT,v INT)", "CREATE TABLE s(id INT,v INT)", "CREATE TABLE u(id INT)",
            "INSERT INTO t VALUES(1,0),(2,0)", "INSERT INTO s VALUES(1,10),(2,20),(2,20)", "INSERT INTO u VALUES(1),(2)" })
        { await session.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        await session.ExecuteAsync("INSERT INTO prior VALUES(1)", cancellationToken: timeout.Token);
        await using var probeSession = await database.CreateSessionAsync(timeout.Token);
        await using var probe = await probeSession.BeginTransactionAsync(timeout.Token);
        database.Catalog.TryGetTable("dbo", "s", out var source).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "prior", out var prior).ShouldBeTrue();

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            subquery ? "UPDATE t SET v=s.v FROM s WHERE t.id=s.id AND EXISTS(SELECT 1 FROM s AS z JOIN u ON z.id=u.id)"
                : "WITH q AS (SELECT s.id,s.v FROM s JOIN u ON s.id=u.id) UPDATE t SET v=q.v FROM q WHERE t.id=q.id", cancellationToken: timeout.Token));

        // Assert: probe while the rejected transaction remains active.
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05068");
        transaction.State.ShouldBe(TransactionState.Active);
        database.Coordinator.LockManager.TryAcquire(probe.Context.Sequence, LockResource.Object(source!.ObjectId), LockMode.Exclusive).ShouldBeTrue();
        database.Coordinator.LockManager.TryAcquire(probe.Context.Sequence, LockResource.Object(prior!.ObjectId), LockMode.Exclusive).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: hash builds enforce statement memory before writes (#1714)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HashBudget_ShouldRefuseBeforeMutation(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dml-hash-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "t", "s" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT,v INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1,100).Select(i => $"({i},0)")), cancellationToken: CancellationToken.None);
        }

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(delete
            ? "DELETE FROM t USING s WHERE t.id=s.id" : "UPDATE t SET v=1 FROM s WHERE t.id=s.id", cancellationToken: CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t WHERE v=0"))[0][0].ShouldBe(100L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DELETE USING: cascade retry reacquires source intent before completion (#1714)")]
    public async Task ExecuteAsync_CascadeRetry_ShouldRetainSourceIntent()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("dml-retry-intent", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in new[] { "CREATE TABLE t(id INT PRIMARY KEY)", "CREATE TABLE s(id INT)",
            "CREATE TABLE c(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON DELETE CASCADE)",
            "INSERT INTO t VALUES(1)", "INSERT INTO s VALUES(1)", "INSERT INTO c VALUES(1,1)" })
        { await setup.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        await using var holderSession = await database.CreateSessionAsync(timeout.Token);
        await using var holder = await holderSession.BeginTransactionAsync(timeout.Token);
        await holderSession.ExecuteAsync("UPDATE c SET id=2", cancellationToken: timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        var pending = session.ExecuteAsync("DELETE FROM t USING s WHERE t.id=s.id", cancellationToken: timeout.Token).AsTask();

        // Act
        try
        {
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);
            await holder.RollbackAsync(timeout.Token);
            (await pending).AffectedCount.ShouldBe(1);

            // Assert: a source DDL grant is excluded until the successful DELETE ends.
            database.Catalog.TryGetTable("dbo", "s", out var source).ShouldBeTrue();
            var probe = new TransactionSequence(ulong.MaxValue - 1714);
            try
            {
                database.Coordinator.LockManager.TryAcquire(probe, LockResource.Object(source!.ObjectId), LockMode.Exclusive).ShouldBeFalse();
                await transaction.RollbackAsync(timeout.Token);
                database.Coordinator.LockManager.TryAcquire(probe, LockResource.Object(source.ObjectId), LockMode.Exclusive).ShouldBeTrue();
            }
            finally { database.Coordinator.LockManager.ReleaseAll(probe); }
        }
        finally
        {
            if (holder.State == TransactionState.Active) { await holder.RollbackAsync(CancellationToken.None); }
            await pending;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Joined DML: source and reference waits release new target grants for FK DDL (#1714)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_SourceIntentWait_ShouldAllowReferencedTargetDdlLock(bool delete, bool reference)
    {
        // Arrange: reproduce FK DDL's own-source-X then referenced-target-X order.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("dml-intent-order", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in new[] { "CREATE TABLE t(id INT PRIMARY KEY,v INT)", "CREATE TABLE s(id INT)",
            "CREATE TABLE c(pid INT REFERENCES t(id))", "INSERT INTO t VALUES(1,0)", "INSERT INTO s VALUES(1)" })
        { await setup.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        database.Catalog.TryGetTable("dbo", reference ? "c" : "s", out var source).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "t", out var target).ShouldBeTrue();
        var locks = database.Coordinator.LockManager;
        var ddl = new TransactionSequence(ulong.MaxValue - 1714);
        await locks.AcquireAsync(ddl, LockResource.Object(source!.ObjectId), LockMode.Exclusive, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        string prefix = reference ? "WITH q AS (SELECT s.id FROM s JOIN t AS z ON z.id=s.id) " : string.Empty;
        string input = reference ? "q" : "s";
        var pending = session.ExecuteAsync(prefix + (delete ? $"DELETE FROM t USING {input} WHERE t.id={input}.id"
            : $"UPDATE t SET v={input}.id FROM {input} WHERE t.id={input}.id"), cancellationToken: timeout.Token).AsTask();

        // Act
        try
        {
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);

            // Assert: the waiter cannot prevent DDL from finishing its lock order.
            locks.TryAcquire(ddl, LockResource.Object(target!.ObjectId), LockMode.Exclusive).ShouldBeTrue();
        }
        finally
        {
            locks.ReleaseAll(ddl);
            (await pending).AffectedCount.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DELETE USING: WITH grants join cascade back-off and volatile sources stay materialized (#1714)")]
    public async Task ExecuteAsync_CteCascadeRetry_ShouldReleaseAndReacquireWrapperGrants()
    {
        // Arrange: hold only the child row, leaving object probes independent of the holder.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("dml-cte-retry");
        builder.Functions.Add(SqlScalarFunction.Create("source_probe", (long value) => { calls++; return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        foreach (string sql in new[] { "CREATE TABLE t(id INT PRIMARY KEY)", "CREATE TABLE s(id INT)",
            "CREATE TABLE c(pid INT REFERENCES t(id) ON DELETE CASCADE)",
            "INSERT INTO t VALUES(1)", "INSERT INTO s VALUES(1)", "INSERT INTO c VALUES(1)" })
        { await setup.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "t", out var target).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "s", out var source).ShouldBeTrue();
        var coordinator = database.Coordinator;
        var holder = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        LockResource childRow;
        using (var rows = database.DataStorage.GetUnitIterator(child.ObjectId))
        {
            rows.MoveNext().ShouldBeTrue();
            var row = rows.Current;
            childRow = LockResource.Entry(child.ObjectId, SqlRecordLocation.Pack(row.PageId, row.SlotIndex));
        }
        await coordinator.LockManager.AcquireAsync(holder.Sequence, childRow, LockMode.Exclusive, timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        var pending = session.ExecuteAsync("WITH q AS (SELECT source_probe(s.id) AS id FROM s JOIN t AS z ON z.id=s.id) " +
            "DELETE FROM t USING q WHERE t.id=q.id", cancellationToken: timeout.Token).AsTask();
        var probe = new TransactionSequence(ulong.MaxValue - 1714);

        // Act / Assert: both wrapper grants release while the DELETE remains in its wait.
        try
        {
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);
            coordinator.LockManager.TryAcquire(probe, LockResource.Object(target.ObjectId), LockMode.Exclusive).ShouldBeTrue();
            coordinator.LockManager.TryAcquire(probe, LockResource.Object(source.ObjectId), LockMode.Exclusive).ShouldBeTrue();
        }
        finally
        {
            coordinator.LockManager.ReleaseAll(probe);
            await coordinator.RollbackAsync(holder, CancellationToken.None);
            (await pending).AffectedCount.ShouldBe(1);
        }
        calls.ShouldBe(1);
        try { coordinator.LockManager.TryAcquire(probe, LockResource.Object(source.ObjectId), LockMode.Exclusive).ShouldBeFalse(); }
        finally { coordinator.LockManager.ReleaseAll(probe); }
    }

    private static async Task<object?[][]> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }
}
