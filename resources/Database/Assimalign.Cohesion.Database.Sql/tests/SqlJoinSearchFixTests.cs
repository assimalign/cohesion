using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJoinSearchFixTests
{
    private readonly ITestOutputHelper _output;

    public SqlJoinSearchFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search fix: identity pushdown removes the ninth-relation cliff (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_WrittenGreedyOrder_ShouldPushWhereAtLastBinding(bool mixedOn)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-identity", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE one_row(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO one_row VALUES(1,1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE tail(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await InsertAsync(session, "tail", 40);
        string from = "one_row r0";
        for (int i = 1; i < 16; i++)
        {
            string table = i < 14 ? "one_row" : "tail";
            from += mixedOn && i < 14 ? $" JOIN {table} r{i} ON r{i - 1}.k=r{i}.k" : $",{table} r{i}";
        }
        string where = string.Join(" AND ", Enumerable.Range(mixedOn ? 14 : 1, mixedOn ? 2 : 15).Select(i => $"r{i - 1}.k=r{i}.k"));
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql($"SELECT r0.id FROM {from} WHERE {where}").Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var before = new SqlStatementContext(transaction, database.Coordinator);
            var after = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, after, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();
            (await DrainAsync(executor, selected, after)).ShouldBe(await DrainAsync(executor, written, before));

            // Assert
            selected.Accepted.ShouldBeTrue();
            selected.Reordered.ShouldBeFalse();
            selected.FirstBinding.ShouldBe(0);
            selected.Steps.Select(step => step.Binding).ShouldBe(Enumerable.Range(1, 15));
            selected.Where.ShouldBeNull();
            selected.CandidateCost.ShouldBeLessThan(selected.BoundCost);
            after.Metrics.JoinCandidatePairs.ShouldBe(93);
            before.Metrics.JoinCandidatePairs.ShouldBe(1653);
            _output.WriteLine($"bound={selected.BoundCost:R}; candidate={selected.CandidateCost:R}; pairs={after.Metrics.JoinCandidatePairs}; examined={after.Metrics.RecordsExamined}; peak={after.Metrics.PeakHashMemoryBytes}; order={selected.ChosenOrder}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search fix: complete, incomplete and unknown drivers never increase retained loop buffers (#1629)")]
    [InlineData(8, 100, true)]
    [InlineData(9, 100, true)]
    [InlineData(8, 100000, true)]
    [InlineData(9, 100000, true)]
    [InlineData(8, 100000, false)]
    [InlineData(9, 100000, false)]
    public async Task Cost_StreamingDriver_ShouldPreserveBufferMemory(int count, int bigRows, bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-loop-memory", new() { StatementMemoryBudget = 5000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE big(id INT{(indexed ? " PRIMARY KEY" : "")},k INT)", cancellationToken: CancellationToken.None);
        await InsertAsync(session, "big", bigRows);
        await session.ExecuteAsync("CREATE TABLE small(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO small VALUES(1,1)", cancellationToken: CancellationToken.None);
        string from = "big b," + string.Join(',', Enumerable.Range(1, count - 1).Select(i => $"small c{i}"));
        string predicate = $"b.k{(count <= 8 ? "<=" : "=")}c{count - 1}.k AND " + string.Join(" AND ", Enumerable.Range(1, count - 2).Select(i => $"c{i}.k=c{i + 1}.k"));
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql($"SELECT b.id FROM {from} WHERE {predicate}").Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None).ShouldBeAssignableTo<SqlJoinPlan>();

            // Assert: the old driver has 100,000 decoded rows; each retained inner has one.
            selected.Tree.ShouldBeNull();
            selected.FirstBinding.ShouldBe(0);
            if (selected is SqlHeuristicJoinPlan heuristic)
            {
                heuristic.BoundMemory.ShouldBeGreaterThan(0);
                heuristic.CandidateMemory.ShouldBeLessThanOrEqualTo(heuristic.BoundMemory);
                heuristic.CandidateMemory.ShouldBe((count - 1) * 248d); // one decoded row and list/growth storage per inner
            }
            (await DrainAsync(executor, selected, context)).ShouldBe(new[] { "1" });
            context.Metrics.PeakHashMemoryBytes.ShouldBe(0);
            _output.WriteLine($"relations={count}; big_rows={bigRows}; probe={selected.InputEstimates![0]}; pairs={context.Metrics.JoinCandidatePairs}; examined={context.Metrics.RecordsExamined}; retained_inner_rows={count - 1}; retained_payload_bytes={(count - 1) * 104}; estimated_peak_retained_bytes={(count - 1) * 248}; peak_hash={context.Metrics.PeakHashMemoryBytes}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search fix: lower estimated memory cannot justify buffering an unbounded driver (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_UnboundedDriverWithUnknownInners_ShouldKeepStreamingEvenWhenMemoryLooksSmaller(bool indexed)
    {
        // Arrange: the three-relation BETWEEN cannot seed the connected component.
        // Unindexed singleton inners estimate 4096, above the indexed driver's 1024
        // lower-bound estimate. Moving that driver last appears to SAVE memory.
        await using var engine = SqlDatabaseEngine.Create("search-unbounded-driver", new() { StatementMemoryBudget = 5000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE big(id INT{(indexed ? " PRIMARY KEY" : "")},k INT)", cancellationToken: CancellationToken.None);
        await InsertAsync(session, "big", 100000);
        await session.ExecuteAsync("CREATE TABLE small(id INT,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO small VALUES(1,1)", cancellationToken: CancellationToken.None);
        string from = "big b," + string.Join(',', Enumerable.Range(1, 8).Select(i => $"small c{i}"));
        string predicate = "b.k BETWEEN c7.k AND c8.k AND " + string.Join(" AND ", Enumerable.Range(1, 7).Select(i => $"c{i}.k=c{i + 1}.k"));
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql($"SELECT b.id FROM {from} WHERE {predicate}").Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();

            // Assert
            if (indexed) { selected.InputEstimates![0]!.Value.IsComplete.ShouldBeFalse(); }
            else { selected.InputEstimates![0].ShouldBeNull(); }
            selected.InputEstimates!.Skip(1).ShouldAllBe(estimate => estimate == null);
            selected.FirstBinding.ShouldBe(0);
            selected.Accepted.ShouldBeTrue(); // certified identity pushdown remains available
            selected.CandidateMemory.ShouldBe(selected.BoundMemory);
            (await DrainAsync(executor, selected, context)).ShouldBe(new[] { "1" });
            context.Metrics.JoinCandidatePairs.ShouldBe(800000);
            context.Metrics.PeakHashMemoryBytes.ShouldBe(0);
            _output.WriteLine($"probe={selected.InputEstimates![0]}; retained_estimate={selected.CandidateMemory:R}; pairs={context.Metrics.JoinCandidatePairs}; examined={context.Metrics.RecordsExamined}; peak_hash={context.Metrics.PeakHashMemoryBytes}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search fix: normalized MERGE skips candidates and compares the written tree (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_NormalizedMerge_ShouldKeepWrittenOrderWithoutSearch(bool insert)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-merge-bound", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,1)", cancellationToken: CancellationToken.None);
        string source = string.Join(" CROSS JOIN ", Enumerable.Range(0, 9).Select(i => $"t r{i}"));
        string action = insert ? "WHEN NOT MATCHED THEN INSERT(id,k) VALUES(r0.id,r0.k)" : "WHEN MATCHED THEN DELETE";
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql($"MERGE INTO t USING {source} ON t.id=r0.id {action}").Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None).ShouldBeOfType<SqlMergePlan>();
            var evidence = selected.Source.Associations.ShouldBeOfType<SqlHeuristicJoinPlan>();

            // Assert
            evidence.Accepted.ShouldBeFalse();
            evidence.Reordered.ShouldBeFalse();
            evidence.States.ShouldBe(0);
            evidence.ComponentPairs.ShouldBe(0);
            evidence.CostEvaluations.ShouldBe(0);
            _output.WriteLine($"order={evidence.ChosenOrder}; states={evidence.States}; pairs={evidence.ComponentPairs}; evaluations={evidence.CostEvaluations}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search fix: near costs use memory then ordinals on every platform (#1629)")]
    public void Find_NearTies_ShouldUseDeterministicSecondaryKeys()
    {
        // Arrange: both differences are below the documented relative cost tolerance.
        double[] rows = [1, 1, 1];

        // Act
        var greedy = SqlGreedyJoinSearch.Find(rows, (_, inner) => (inner == 1 ? 1 + 1e-13 : 1, 0, true), CancellationToken.None);
        var exact = SqlJoinSearch.Find(2, _ => 1, (left, _) => (new(0, SqlJoinType.Inner, null, null), left == 1 ? 1 + 1e-13 : 1), CancellationToken.None);

        // Assert
        greedy.Order.ShouldBe(new[] { 0, 1, 2 });
        exact.Tree.Leaves().ShouldBe(new[] { 0, 1 });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search fix: old index and hash paths avoid comparison allocations (#1629)")]
    public async Task Cost_OldPhysicalPaths_ShouldBoundPlanningAllocation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("search-old-paths", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string name in new[] { "a", "b", "c" })
        {
            await session.ExecuteAsync($"CREATE TABLE {name}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await InsertAsync(session, name, 80);
        }
        await session.ExecuteAsync("CREATE TABLE s(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO s VALUES(1,1)", cancellationToken: CancellationToken.None);
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT a.id FROM a JOIN b ON a.k=b.k JOIN c ON b.id=c.id" + string.Concat(Enumerable.Range(0, 6).Select(i => $" CROSS JOIN s r{i}"))).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);
            _ = executor.Cost(written, context, CancellationToken.None);
            long minimum = long.MaxValue;
            SqlHeuristicJoinPlan? chosen = null;

            // Act
            for (int round = 0; round < 3; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                chosen = (SqlHeuristicJoinPlan)executor.Cost(written, context, CancellationToken.None);
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            // Assert
            chosen!.Steps.ShouldContain(step => step.Hash != null);
            chosen.Steps.ShouldContain(step => step.Access != null);
            _output.WriteLine($"old_paths_planning_bytes={minimum}; accepted={chosen.Accepted}; pairs={chosen.ComponentPairs}; evaluations={chosen.CostEvaluations}");
#if DEBUG
            minimum.ShouldBeLessThanOrEqualTo(21_976);
#else
            minimum.ShouldBeLessThanOrEqualTo(21_328);
#endif
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    private static async Task InsertAsync(SqlDatabaseSession session, string table, int count)
    {
        for (int first = 1; first <= count; first += 1000)
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES" + string.Join(',', Enumerable.Range(first, Math.Min(1000, count - first + 1))
                .Select(i => $"({i},{i})")), cancellationToken: CancellationToken.None);
        }
    }

    private static async Task<string[]> DrainAsync(SqlPlanExecutor executor, SqlPlan plan, SqlStatementContext context)
    {
        await using var result = (DatabaseResultSet)await executor.ExecuteAsync(plan, context, CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Convert.ToString(row.GetValue(0), CultureInfo.InvariantCulture)!); }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
