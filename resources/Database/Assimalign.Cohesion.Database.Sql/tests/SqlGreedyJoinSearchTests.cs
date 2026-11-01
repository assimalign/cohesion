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
public sealed class SqlGreedyJoinSearchTests
{
    private readonly ITestOutputHelper _output;

    public SqlGreedyJoinSearchTests(ITestOutputHelper output) { _output = output; }

    public static IEnumerable<object[]> MeasurementCases()
    {
        foreach (int count in new[] { 2, 8, 16, 64, 65 })
        { foreach (string shape in new[] { "chain", "star", "clique", "disconnected" }) { yield return [count, shape]; } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: planning work and allocation obey polynomial ceilings (#1629)")]
    [MemberData(nameof(MeasurementCases))]
    public async Task Cost_RepresentativeGraphs_ShouldBoundWorkAndAllocation(int count, string shape)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("greedy-measure", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,1)", cancellationToken: CancellationToken.None);
        string from = string.Join(" CROSS JOIN ", Enumerable.Range(0, count).Select(i => $"t r{i}"));
        string[] predicates = Edges(count, shape).Select(edge => $"r{edge.Left}.k=r{edge.Right}.k").ToArray();
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT r0.id FROM " + from + (predicates.Length == 0 ? "" : " WHERE " + string.Join(" AND ", predicates))).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);
            _ = executor.Cost(written, context, CancellationToken.None);
            long minimumBytes = long.MaxValue;
            SqlPlan? selected = null;
            int probes = 0;
            SqlPlanExecutor.CostProbeCompleted = _ => probes++;
            try
            {
                // Act: minimum allocation of three warmed rounds; no wall-clock assertion.
                for (int round = 0; round < 3; round++)
                {
                    probes = 0;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    selected = executor.Cost(written, context, CancellationToken.None);
                    minimumBytes = Math.Min(minimumBytes, GC.GetAllocatedBytesForCurrentThread() - before);
                }
            }
            finally { SqlPlanExecutor.CostProbeCompleted = null; }

            // Assert
            long pairs = 0, evaluations = 0;
            int states = 0;
            if (count > 64)
            {
                selected.ShouldNotBeOfType<SqlSearchedJoinPlan>();
                probes.ShouldBe(0);
            }
            else if (count > 8)
            {
                var heuristic = selected.ShouldBeOfType<SqlHeuristicJoinPlan>();
                pairs = heuristic.ComponentPairs; evaluations = heuristic.CostEvaluations; states = heuristic.States;
                pairs.ShouldBe((long)(count - 1) * (3 * count - 2) / 2);
                evaluations.ShouldBe(pairs + 2 * (count - 1));
                states.ShouldBe(2 * count - 1);
                probes.ShouldBe(count);
                heuristic.Accepted.ShouldBeTrue(); // identity pushdown is safe, including equal work.
            }
            else if (count == 8)
            {
                var exact = selected.ShouldBeOfType<SqlSearchedJoinPlan>();
                states = exact.States; evaluations = exact.Partitions;
                states.ShouldBe(255); evaluations.ShouldBe(6050);
            }
            else
            {
                // Production's two-input path is intentionally unchanged. Also report
                // the exact kernel's historical 3 states / 2 partitions at this size.
                var kernel = SqlJoinSearch.Find(2, _ => 1, (_, _) => (new(1, SqlJoinType.Inner, null, null), 1), CancellationToken.None);
                kernel.States.ShouldBe(3); kernel.Partitions.ShouldBe(2);
                states = kernel.States; evaluations = kernel.Partitions;
            }
            if (count > 8)
            { minimumBytes.ShouldBeLessThanOrEqualTo(32_768L + 8192L * count + 4096L * predicates.Length); }
            else if (count == 8)
            {
                // Minimum of three warmed rounds on Windows ARM64. Other platforms
                // may allocate less; these measured values are ceilings, not equalities.
#if DEBUG
                long ceiling = shape switch { "chain" => 974_216, "star" => 875_784, "clique" => 2_202_800, _ => 881_728 };
#else
                long ceiling = shape switch { "chain" => 973_304, "star" => 874_872, "clique" => 2_201_384, _ => 880_840 };
#endif
                minimumBytes.ShouldBeLessThanOrEqualTo(ceiling);
            }
            else
            { minimumBytes.ShouldBeLessThanOrEqualTo(32_768L + 8192L * evaluations + 4096L * predicates.Length); }
            _output.WriteLine($"relations={count}; shape={shape}; states={states}; pairs={pairs}; evaluations={evaluations}; probes={probes}; planning_bytes={minimumBytes}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: skewed chain and star reduce execution work and repeat deterministically (#1629)")]
    [InlineData(16, "chain")]
    [InlineData(16, "star")]
    [InlineData(64, "chain")]
    public async Task Cost_SkewedLargeGraph_ShouldChooseCheaperOrder(int count, string shape)
    {
        // Arrange: WHERE edges leave the old loop plan without a new budgeted hash.
        await using var engine = SqlDatabaseEngine.Create("greedy-skew", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < count; i++)
        {
            await session.ExecuteAsync($"CREATE TABLE t{i}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            int rows = i < 2 ? 40 : 1;
            await session.ExecuteAsync($"INSERT INTO t{i} VALUES" + string.Join(',', Enumerable.Range(1, rows).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        string sql = "SELECT t0.id FROM " + string.Join(',', Enumerable.Range(0, count).Select(i => $"t{i}")) + " WHERE " +
            string.Join(" AND ", Edges(count, shape).Select(edge => $"t{edge.Left}.k=t{edge.Right}.k"));
        if (count > 64) { sql += " AND t64.id=1"; }
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var before = new SqlStatementContext(transaction, database.Coordinator);
            var after = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, after, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();
            var expected = await RowsAsync(executor, written, before);
            var actual = await RowsAsync(executor, selected, after);

            // Assert
            actual.ShouldBe(expected); actual.ShouldNotBeEmpty();
            selected.Accepted.ShouldBeTrue(); selected.Reordered.ShouldBe(shape == "star");
            selected.CandidateCost.ShouldBeLessThan(selected.BoundCost);
            selected.CandidateMemory.ShouldBeLessThanOrEqualTo(selected.BoundMemory);
            after.Metrics.JoinCandidatePairs.ShouldBeLessThan(before.Metrics.JoinCandidatePairs);
            for (int repeat = 0; repeat < 5; repeat++)
            {
                var again = executor.Cost(written, after, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();
                again.ChosenOrder.ShouldBe(selected.ChosenOrder);
                again.SearchCost.ShouldBe(selected.SearchCost);
                again.ComponentPairs.ShouldBe(selected.ComponentPairs);
            }
            _output.WriteLine($"shape={shape}; bound_cost={selected.BoundCost}; chosen_cost={selected.CandidateCost}; before_pairs={before.Metrics.JoinCandidatePairs}; after_pairs={after.Metrics.JoinCandidatePairs}; order={selected.ChosenOrder}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: identity pushdown escapes a greedy connected trap (#1629)")]
    public async Task Cost_GreedyTrap_ShouldChooseIdentityPushdown()
    {
        // Arrange: connecting the small 1/4/8 component first is a local minimum.
        int[] counts = [20, 1, 10, 10, 10, 2, 2, 3, 10];
        await using var engine = SqlDatabaseEngine.Create("greedy-trap", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < counts.Length; i++)
        {
            await session.ExecuteAsync($"CREATE TABLE t{i}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO t{i} VALUES" + string.Join(',', Enumerable.Range(1, counts[i]).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT t0.id FROM t0,t1,t2,t3,t4,t5,t6,t7,t8 WHERE t0.k=t2.k AND t0.k=t3.k AND t2.k<t3.k AND t1.k<t4.k AND t0.k<t7.k AND t2.k=t7.k AND t4.k<t8.k").Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var before = new SqlStatementContext(transaction, database.Coordinator);
            var after = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, after, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();
            (await RowsAsync(executor, selected, after)).ShouldBe(await RowsAsync(executor, written, before));

            // Assert
            selected.CandidateCost.ShouldBeLessThan(selected.BoundCost);
            selected.Accepted.ShouldBeTrue(); selected.Reordered.ShouldBeFalse();
            before.Metrics.JoinCandidatePairs.ShouldBe(2_782_220);
            after.Metrics.JoinCandidatePairs.ShouldBe(320);
            selected.FirstBinding.ShouldBe(0);
            selected.Steps.Select(step => step.Binding).ShouldBe(Enumerable.Range(1, 8));
            _output.WriteLine($"bound_cost={selected.BoundCost}; chosen_cost={selected.CandidateCost}; before_pairs={before.Metrics.JoinCandidatePairs}; after_pairs={after.Metrics.JoinCandidatePairs}; examined={after.Metrics.RecordsExamined}; peak={after.Metrics.PeakHashMemoryBytes}");
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: unknown/incomplete large products followed by an empty input have finite costs (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cost_LargeEstimatedProduct_ShouldAvoidOverflowAndNaN(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("greedy-estimates", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t(id INT{(indexed ? " PRIMARY KEY" : "")})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE empty_input(id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        if (indexed)
        { await session.ExecuteAsync("INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(1, 1000).Select(id => $"({id})")), cancellationToken: CancellationToken.None); }
        string from = string.Join(',', Enumerable.Range(0, 63).Select(i => $"t r{i}")) + ",empty_input r63";
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT r0.id FROM " + from).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();

            // Assert
            double.IsFinite(selected.BoundCost).ShouldBeTrue();
            double.IsFinite(selected.CandidateCost).ShouldBeTrue();
            selected.Accepted.ShouldBeTrue(); selected.FirstBinding.ShouldBe(0);
            if (indexed) { selected.InputEstimates!.Take(63).ShouldAllBe(count => count.HasValue && !count.Value.IsComplete); }
            else { selected.InputEstimates!.Take(63).ShouldAllBe(count => count == null); }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: more than eight DML sources keep the target boundary and multiple-match policy (#1629)")]
    [InlineData("update", true)]
    [InlineData("delete", true)]
    [InlineData("update", false)]
    [InlineData("delete", false)]
    [InlineData("merge", false)]
    public async Task ExecuteAsync_LargeDmlGraph_ShouldPreserveTargetAndResults(string operation, bool dependent)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("greedy-dml", new() { StatementMemoryBudget = 100_000 });
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t(id INT PRIMARY KEY,v INT)", "INSERT INTO t VALUES(1,0)",
            "CREATE TABLE s(id INT PRIMARY KEY,k INT)", "INSERT INTO s VALUES(1,1)", "CREATE TABLE d(id INT,k INT)", "INSERT INTO d VALUES(1,1),(1,1)" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        string from = (operation == "update" && !dependent ? "d r0" : "s r0") +
            string.Concat(Enumerable.Range(1, 9).Select(i => $" JOIN s r{i} ON r{i - 1}.k=r{i}.k"));
        string condition = dependent ? "t.id=r9.id" : "t.id=1";
        string query = operation switch
        {
            "update" => $"UPDATE t SET v=r9.k FROM {from} WHERE {condition}",
            "delete" => $"DELETE FROM t USING {from} WHERE {condition}",
            _ => $"MERGE INTO t USING {from} ON t.id=r9.id WHEN MATCHED THEN UPDATE SET v=r9.k",
        };
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(query).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None);
            var source = selected switch { SqlUpdatePlan p => p.Source!, SqlDeletePlan p => p.Source!, SqlMergePlan p => p.Source, _ => throw new InvalidOperationException() };

            // Assert
            if (dependent) { source.Associations.ShouldNotBeOfType<SqlHeuristicJoinPlan>(); }
            else
            {
                var searched = source.Associations.ShouldBeOfType<SqlHeuristicJoinPlan>();
                searched.SourceOnly.ShouldBeTrue(); searched.Accepted.ShouldBe(operation != "merge");
                searched.ComponentPairs.ShouldBe(0);
            }
            if (source.Associations.Tree is { } tree) { tree.Left!.Binding.ShouldBe(0); }
            else { source.Associations.FirstBinding.ShouldBe(0); }
            if (operation == "update" && !dependent)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await executor.ExecuteAsync(selected, context, CancellationToken.None));
                failure.Diagnostics.Single().Code.ShouldBe("CS05068");
            }
            else { (await executor.ExecuteAsync(selected, context, CancellationToken.None)).AffectedCount.ShouldBe(1); }
            context.Metrics.HashMemoryBytes.ShouldBe(0);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    public static IEnumerable<object[]> OracleCases()
    {
        for (int count = 9; count <= 20; count++)
        { for (int variant = 0; variant < 6; variant++) { yield return [count, variant]; } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: 9-20 relation generated NULL/duplicate, non-equi, multi-key and ON/WHERE multisets match written order (#1629)")]
    [MemberData(nameof(OracleCases))]
    public async Task ExecuteAsync_GeneratedLargeGraph_ShouldMatchWrittenOracle(int count, int variant)
    {
        // Arrange: only two inputs fan out; singleton suffixes keep the exhaustive
        // written-order oracle small while exercising every binding, including NULLs.
        await using var engine = SqlDatabaseEngine.Create("greedy-oracle", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var random = new Random(count * 31 + variant);
        for (int i = 0; i < count; i++)
        {
            await session.ExecuteAsync($"CREATE TABLE t{i}(pk INT PRIMARY KEY,id INT,k {(variant % 2 == 0 ? "INT" : "BIGINT")},v INT)", cancellationToken: CancellationToken.None);
            string values = i < 2 ? $"(0,1,1,1),(1,1,1,1),(2,2,{random.Next(2)},0),(3,3,NULL,1)"
                : variant == 5 ? "(0,1,NULL,1)" : "(0,1,1,1)";
            await session.ExecuteAsync($"INSERT INTO t{i} VALUES{values}", cancellationToken: CancellationToken.None);
        }
        string Predicate(int i) => variant switch
        {
            2 => $"t{i - 1}.k<=t{i}.k",
            3 => $"t{i - 1}.k=t{i}.k AND t{i - 1}.v=t{i}.v",
            5 => $"t{i - 1}.k IS NOT DISTINCT FROM t{i}.k",
            _ => $"t{i - 1}.k=t{i}.k",
        };
        string from = "t0";
        for (int i = 1; i < count; i++) { from += variant % 2 == 0 ? $" CROSS JOIN t{i}" : $" JOIN t{i} ON {Predicate(i)}"; }
        string sql = $"SELECT t0.id,t1.id,t{count - 1}.k FROM {from}" + (variant % 2 == 0 && variant != 4
            ? " WHERE " + string.Join(" AND ", Enumerable.Range(1, count - 1).Select(Predicate)) : "");
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None).ShouldBeOfType<SqlHeuristicJoinPlan>();
            var expected = await RowsAsync(executor, written, context);
            var actual = await RowsAsync(executor, selected, context);

            // Assert
            actual.ShouldBe(expected);
            actual.ShouldNotBeEmpty();
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: large graphs retain every SQ-Z1 legality barrier (#1629)")]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("full")]
    [InlineData("derived")]
    [InlineData("view")]
    [InlineData("system")]
    [InlineData("exists")]
    [InlineData("not_exists")]
    [InlineData("in")]
    [InlineData("throwing")]
    [InlineData("call")]
    public async Task Cost_LargeBarrierGraph_ShouldKeepBoundOrder(string barrier)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("greedy-barrier");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => value));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW v AS SELECT id,k FROM t", cancellationToken: CancellationToken.None);
        string first = barrier switch { "derived" => "(SELECT id,k FROM t) r0", "view" => "v r0", "system" => "INFORMATION_SCHEMA.TABLES r0", _ => "t r0" };
        string from = first + (barrier is "left" or "right" or "full" ? $" {barrier.ToUpperInvariant()} JOIN t r1 ON r0.k=r1.k" : " CROSS JOIN t r1") +
            string.Concat(Enumerable.Range(2, 10).Select(i => $" CROSS JOIN t r{i}"));
        string predicate = barrier switch
        {
            "exists" => "EXISTS(SELECT 1 FROM t x WHERE x.id=r1.id)",
            "not_exists" => "NOT EXISTS(SELECT 1 FROM t x WHERE x.id=r1.id)",
            "in" => "r1.id IN(SELECT id FROM t)",
            "throwing" => "r1.id/(r2.id-r3.id)>0", "call" => "observe(r1.id)>0", _ => "r1.id=r2.id",
        };
        var written = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(SqlQueryRequest.FromSql("SELECT r1.id FROM " + from + " WHERE " + predicate).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None);

            // Assert: wrappers for subquery predicates retain their original join input.
            var join = selected switch { SqlSubqueryPlan p => p.Input, SqlCorrelatedSubqueryPlan p => p.Input, _ => selected };
            join.ShouldNotBeOfType<SqlHeuristicJoinPlan>();
            if (join is SqlJoinPlan flat && flat.Tree is null) { flat.Steps.Select(step => step.Binding).ShouldBe(Enumerable.Range(1, 11)); }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: large volatile and sequence predicates keep exact written evaluation counts (#1629)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_LargeVolatileGraph_ShouldPreserveCallCounts(bool where, bool sequence)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("greedy-calls");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => { calls++; return value; }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string sql in new[] { "CREATE TABLE t(id INT)", "INSERT INTO t VALUES(1)", "CREATE TABLE a(id INT)",
            "INSERT INTO a VALUES(1),(2)", "CREATE TABLE b(id INT)", "INSERT INTO b VALUES(1),(2),(3)", "CREATE SEQUENCE s" })
        { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
        string predicate = sequence ? "nextval('s')>0" : "observe(a.id)>0";
        string sqlQuery = "SELECT a.id FROM a JOIN t r0 ON " + (where ? "TRUE" : predicate) + " CROSS JOIN b" +
            string.Concat(Enumerable.Range(1, 9).Select(i => $" CROSS JOIN t r{i}")) + (where ? " WHERE " + predicate : "");

        // Act
        int rows = 0;
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync(sqlQuery, cancellationToken: CancellationToken.None))
        { await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; } }
        if (sequence)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT currval('s')", cancellationToken: CancellationToken.None);
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { calls = Convert.ToInt32(row.GetValue(0)); }
        }

        // Assert
        rows.ShouldBe(6); calls.ShouldBe(where ? 6 : 2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Greedy search: connected joins precede products and ordinal ties exceed mask width (#1629)")]
    public void Find_DisconnectedComponents_ShouldDelayProductsAndRetainAllOrdinals()
    {
        // Arrange
        const int count = 65;
        var rows = Enumerable.Repeat(1d, count).ToArray();

        // Act
        var result = SqlGreedyJoinSearch.Find(rows, (component, inner) =>
        {
            bool connected = inner == 64 && component[0] || inner == 0 && component[64];
            return (1, 0, connected);
        }, CancellationToken.None);

        // Assert
        result.Order.Take(2).ShouldBe(new[] { 0, 64 });
        result.Order.Skip(2).ShouldBe(Enumerable.Range(1, 63));
        result.Order.Distinct().Count().ShouldBe(count);
        result.Pairs.ShouldBe((long)(count - 1) * (3 * count - 2) / 2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: large LATERAL retains the dialect refusal (#1629)")]
    public void Plan_LargeLateralGraph_ShouldRetainUnsupportedDiagnostic()
    {
        // Arrange
        string query = "SELECT r0.id FROM " + string.Join(" CROSS JOIN ", Enumerable.Range(0, 9).Select(i => $"t r{i}")) +
            " CROSS JOIN LATERAL (SELECT r0.id) x";

        // Act
        var failure = Should.Throw<DatabaseParseException>(() => SqlQueryRequest.FromSql(query));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    private static IEnumerable<(int Left, int Right)> Edges(int count, string shape)
    {
        for (int right = 1; right < count; right++)
        {
            if (shape == "clique") { for (int left = 0; left < right; left++) { yield return (left, right); } }
            else if (shape == "star") { yield return (0, right); }
            else if (shape != "disconnected" || right != count / 2) { yield return (right - 1, right); }
        }
    }

    private static async Task<string[]> RowsAsync(SqlPlanExecutor executor, SqlPlan plan, SqlStatementContext context)
    {
        await using var result = (DatabaseResultSet)await executor.ExecuteAsync(plan, context, CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(string.Join('|', Enumerable.Range(0, result.Columns.Count).Select(i => row.IsNull(i) ? "NULL" : Convert.ToString(row.GetValue(i), CultureInfo.InvariantCulture)))); }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
