using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlPlannerReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlPlannerReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Review: costing reads latched indexes without metadata grants and skips joins without alternatives")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_UnneededMetadataLocks_ShouldAvoidManagerAcquisitions(bool nonEquality)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("review-probe-lock", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_k ON t(k)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1,1),(2,2)", cancellationToken: CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        var blocker = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        int probes = 0;
        try
        {
            if (!nonEquality)
            { database.Coordinator.LockManager.TryAcquire(blocker.Sequence, LockResource.Object(table.ObjectId), LockMode.Exclusive).ShouldBeTrue(); }
            var request = SqlQueryRequest.FromSql(nonEquality ? "SELECT a.id FROM t a JOIN t b ON a.k>b.k" : "SELECT id FROM t WHERE k=1");
            var plan = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression);
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            SqlPlanExecutor.CostProbeCompleted = _ => probes++;

            // Act
            executor.Cost(plan, new SqlStatementContext(reader, database.Coordinator), CancellationToken.None);

            // Assert: an X grant cannot block this safe tree read; a nonequality needs no estimates.
            probes.ShouldBe(nonEquality ? 0 : 1);
        }
        finally
        {
            SqlPlanExecutor.CostProbeCompleted = null;
            await database.Coordinator.RollbackAsync(reader, CancellationToken.None);
            await database.Coordinator.RollbackAsync(blocker, CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review: small nonunique lookups and unique FK probes avoid unnecessary costing work")]
    public async Task ExecuteAsync_CommonQueries_ShouldBoundProbeWork()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("review-cost", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync("CREATE TABLE orders(id INT PRIMARY KEY,cust INT)");
        await ExecuteAsync("CREATE INDEX by_cust ON orders(cust)");
        for (int start = 0; start < 100_000; start += 500)
        {
            await ExecuteAsync("INSERT INTO orders VALUES " + string.Join(',', Enumerable.Range(start, 500).Select(id => $"({id},{id % 10000})")));
        }
        await ExecuteAsync("CREATE TABLE customers(id INT PRIMARY KEY)");
        await ExecuteAsync("CREATE TABLE children(id INT PRIMARY KEY,cust INT REFERENCES customers(id))");
        await ExecuteAsync("INSERT INTO customers VALUES (42)");
        await ExecuteAsync("INSERT INTO children VALUES (1,42)");
        var queries = new[] { "SELECT id FROM orders WHERE cust=42", "UPDATE orders SET cust=42 WHERE cust=42",
            "DELETE FROM orders WHERE cust=42", "SELECT c.id FROM children c JOIN customers p ON c.cust=p.id WHERE c.id=1" };
        var probeCounts = new List<int>();
        foreach (string sql in queries)
        {
            var request = SqlQueryRequest.FromSql(sql);
            long minimumBytes = long.MaxValue;
            int probes = 0, pages = 0, entries = 0;
            for (int sample = -1; sample < 3; sample++)
            {
                await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
                probes = pages = entries = 0;
                SqlPlanExecutor.CostProbeCompleted = count => { probes++; pages += count.PagesVisited; entries += count.EntriesExamined; };
                try
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    var pending = session.ExecuteAsync(request, CancellationToken.None);
                    pending.IsCompletedSuccessfully.ShouldBeTrue();
                    var result = await pending;
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (sample >= 0) { minimumBytes = Math.Min(minimumBytes, bytes); }
                    if (result is DatabaseResultSet rows)
                    {
                        int count = 0;
                        await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { count++; }
                        count.ShouldBe(sql.Contains("JOIN", StringComparison.Ordinal) ? 1 : 10);
                        await rows.DisposeAsync();
                    }
                    else { result.AffectedCount.ShouldBe(10); }
                }
                finally { SqlPlanExecutor.CostProbeCompleted = null; }
                await transaction.RollbackAsync(CancellationToken.None);
            }
            _output.WriteLine($"{sql}: probes={probes}; pages={pages}; entries={entries}; bytes/statement={minimumBytes}");
            probeCounts.Add(probes);
        }

        // Assert: the only probe needed is the selected nonunique range; the FK join needs none.
        probeCounts.ShouldBe([1, 1, 1, 0]);

        async Task ExecuteAsync(string sql) => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review: derived text in any ON term preserves nested-loop Unicode errors")]
    public async Task ExecuteAsync_DerivedTextResidual_ShouldPreserveNestedLoopError()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("review-hash-error", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE l(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE r(k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO l VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id})")), cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO r VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(_ => "(NULL)")), cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["s"] = "\ud800" };
        var request = SqlQueryRequest.FromSql("SELECT a.id FROM (SELECT id,@s AS s FROM l) a JOIN r ON a.s='ok' AND a.id=r.k", parameters);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var context = new SqlStatementContext(transaction, database.Coordinator);
            var plan = new SqlPlanner(database.Catalog, parameters, database.Definitions.Functions).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act / Assert: first prove the original loop throws, then compare the costed plan.
            var nested = await Should.ThrowAsync<DatabaseTypeException>(() => executor.ExecuteAsync(plan, context, CancellationToken.None));
            var costed = executor.Cost(plan, context, CancellationToken.None).ShouldBeOfType<SqlJoinPlan>();
            var optimized = await Should.ThrowAsync<DatabaseTypeException>(() => executor.ExecuteAsync(costed, context, CancellationToken.None));
            optimized.Message.ShouldBe(nested.Message);
            costed.Steps[0].Hash.ShouldBeNull();
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Review: a single derived input reports outer candidates")]
    public async Task ExecuteAsync_DerivedAnalysis_ShouldCountOuterCandidates()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("review-derived-analysis", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1),(2)", cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "EXPLAIN ANALYZE SELECT v.id FROM (SELECT id FROM t) v WHERE v.id>0");

        // Assert
        Convert.ToInt64(rows[0][7]).ShouldBe(2);
        session.LastStatementMetrics.ShouldNotBeNull().JoinCandidatePairs.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Review: repeated view references report independent child counts")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RepeatedViewAnalysis_ShouldCountEachReference(bool nested)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("review-view-analysis", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1),(2),(3)", cancellationToken: CancellationToken.None);
        if (nested)
        { await session.ExecuteAsync("CREATE VIEW w AS SELECT id FROM t", cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE VIEW v AS SELECT id FROM " + (nested ? "w" : "t"), cancellationToken: CancellationToken.None);

        // Act
        var rows = await RowsAsync(session, "EXPLAIN ANALYZE SELECT a.id FROM v a JOIN v b ON a.id=b.id");

        // Assert
        var children = rows.Where(row => Equals(row[2], "Select") && Equals(row[3], "dbo.t")).ToArray();
        children.Length.ShouldBe(2);
        children.Select(row => Convert.ToInt64(row[7])).ShouldBe([3, 3]);
        rows.Where(row => Equals(row[2], "TableScan")).Select(row => Convert.ToInt64(row[7])).ShouldBe([3, 3]);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
