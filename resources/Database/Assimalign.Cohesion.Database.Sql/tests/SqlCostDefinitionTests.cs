using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlCostDefinitionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Costing: optional fallback retains mandatory mutation definition checks (#1740)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_ChangedMutationDefinition_ShouldRetainExecutionRefusal(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cost-definition-writer", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,b VARCHAR(10),c INT)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE INDEX by_c ON t(c)", cancellationToken: CancellationToken.None);
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(delete
            ? "DELETE FROM t WHERE c=7" : "UPDATE t SET c=8 WHERE c=7").Statement.SqlExpression);
        await writer.ExecuteAsync("ALTER TABLE t DROP COLUMN b", cancellationToken: CancellationToken.None);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var context = new SqlStatementContext(transaction, database.Coordinator);
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        try
        {
            // Act
            var costed = executor.Cost(plan, context, CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseTransactionAbortedException>(() => executor.ExecuteAsync(costed, context, CancellationToken.None));

            // Assert
            costed.ShouldBeSameAs(plan);
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04006");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Costing: DROP COLUMN after a completed probe preserves the SELECT's bound shape (#1740)")]
    public async Task ExecuteAsync_DefinitionChangedAfterProbe_ShouldReadBoundShape()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cost-definition-reader", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);
        await writer.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,b VARCHAR(10),c INT)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("CREATE INDEX by_c ON t(c)", cancellationToken: CancellationToken.None);
        await writer.ExecuteAsync("INSERT INTO t VALUES (1,'old',7)", cancellationToken: CancellationToken.None);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int probes = 0;
        SqlPlanExecutor.CostProbeCompleted = _ =>
        {
            Interlocked.Increment(ref probes);
            completed.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        };
        Task<DatabaseResult> select = Task.Factory.StartNew(async () =>
            await reader.ExecuteAsync("SELECT * FROM t WHERE c=7", cancellationToken: CancellationToken.None),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        try
        {
            // Act: the completion event holds the reader until another session commits DDL.
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await writer.ExecuteAsync("ALTER TABLE t DROP COLUMN b", cancellationToken: CancellationToken.None);
            release.Set();
            await using var result = (await select.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeAssignableTo<DatabaseResultSet>();

            // Assert
            Volatile.Read(ref probes).ShouldBe(1);
            result.Columns.Select(column => column.Name).ShouldBe(["id", "b", "c"]);
            var rows = new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
            rows.ShouldHaveSingleItem().ShouldBe([1, "old", 7]);
        }
        finally
        {
            release.Set();
            try { await select.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { SqlPlanExecutor.CostProbeCompleted = null; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Costing: changed definitions discard seek, scan, join direction and hash estimates (#1740)")]
    [InlineData("seek", 0)]
    [InlineData("seek", 1)]
    [InlineData("alternatives", 0)]
    [InlineData("alternatives", 2)]
    [InlineData("scan", 0)]
    [InlineData("scan", 2)]
    [InlineData("join", 0)]
    [InlineData("join", 2)]
    [InlineData("hash", 0)]
    [InlineData("hash", 2)]
    [InlineData("search", 0)]
    [InlineData("search", 2)]
    [InlineData("searchEarlier", 2)]
    public async Task Cost_DefinitionChangedBeforeOrDuringProbe_ShouldKeepBoundPlan(string shape, int dropAfterProbe)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cost-definition-plans", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var writer = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in shape.StartsWith("search", StringComparison.Ordinal) ? new[] { "t", "s", "r" } : new[] { "t", "s" })
        {
            await writer.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,b VARCHAR(10),c INT,d INT)", cancellationToken: CancellationToken.None);
            if (shape != "hash")
            {
                await writer.ExecuteAsync($"CREATE INDEX by_c ON {table}(c)", cancellationToken: CancellationToken.None);
                await writer.ExecuteAsync($"CREATE INDEX by_d ON {table}(d)", cancellationToken: CancellationToken.None);
            }
            await writer.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, 80).Select(id => $"({id},'old',7,7)")), cancellationToken: CancellationToken.None);
        }
        string sql = shape switch
        {
            "alternatives" => "SELECT * FROM t WHERE c=7 AND d=7",
            "join" or "hash" => "SELECT t.id,s.id FROM t JOIN s ON t.c=s.c",
            "search" or "searchEarlier" => "SELECT t.id,s.id,r.id FROM t JOIN s ON t.c=s.c JOIN r ON s.c=r.c",
            _ => "SELECT * FROM t WHERE c=7",
        };
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var context = new SqlStatementContext(transaction, database.Coordinator);
        var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int probes = 0;
        SqlPlanExecutor.CostProbeCompleted = _ =>
        {
            if (Interlocked.Increment(ref probes) != dropAfterProbe) { return; }
            completed.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        };
        Task<SqlPlan>? costing = null;
        try
        {
            if (dropAfterProbe == 0)
            { await writer.ExecuteAsync("ALTER TABLE t DROP COLUMN b", cancellationToken: CancellationToken.None); }

            // Act
            costing = Task.Factory.StartNew(() => executor.Cost(plan, context, CancellationToken.None),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (dropAfterProbe > 0)
            {
                await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                // The second join probe observes s; other probes observe t.
                string changed = shape is "join" or "hash" or "search" ? "s" : "t";
                await writer.ExecuteAsync($"ALTER TABLE {changed} DROP COLUMN b", cancellationToken: CancellationToken.None);
                release.Set();
            }
            var costed = await costing.WaitAsync(TimeSpan.FromSeconds(30));

            // Assert: even earlier successful estimates must not change the original access path.
            costed.ShouldBeSameAs(plan);
            Volatile.Read(ref probes).ShouldBe(shape == "searchEarlier" ? 3 : dropAfterProbe);
        }
        finally
        {
            release.Set();
            try { if (costing is not null) { await costing.WaitAsync(TimeSpan.FromSeconds(30)); } }
            finally
            {
                SqlPlanExecutor.CostProbeCompleted = null;
                await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
            }
        }
    }
}
