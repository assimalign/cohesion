using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlJoinOrderSearchTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: target-parameterized DML retains constant indexed row visits (#1629)")]
    [InlineData(16, false)]
    [InlineData(16, true)]
    [InlineData(500, false)]
    [InlineData(500, true)]
    [InlineData(2000, false)]
    [InlineData(2000, true)]
    public async Task ExecuteAsync_TargetParameterizedSources_ShouldRetainProbeWork(int count, bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-target-probes", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,0)", cancellationToken: CancellationToken.None);
        foreach (string table in new[] { "a", "b" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
            for (int start = 1; start <= count; start += 500)
            {
                await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',',
                    Enumerable.Range(start, Math.Min(500, count - start + 1)).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
            }
        }

        // Act
        var result = await session.ExecuteAsync(delete
            ? "DELETE FROM t USING a JOIN b ON a.id=b.id WHERE t.id=a.id"
            : "UPDATE t SET v=b.v FROM a JOIN b ON a.id=b.id WHERE t.id=a.id", cancellationToken: CancellationToken.None);

        // Assert: one target and one primary-key probe per source, independently of source size.
        result.AffectedCount.ShouldBe(1);
        session.LastStatementMetrics.ShouldNotBeNull().RecordsExamined.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: each input estimate is probed once and reused by every partition (#1629)")]
    [InlineData(4, 15, 50)]
    [InlineData(6, 63, 602)]
    [InlineData(8, 255, 6050)]
    public async Task Cost_ExactGraphs_ShouldReuseInputProbes(int count, int states, int partitions)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-search-probes", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < count; i++)
        { await session.ExecuteAsync($"CREATE TABLE t{i}(id INT PRIMARY KEY)", cancellationToken: CancellationToken.None); }
        string from = string.Join(" CROSS JOIN ", Enumerable.Range(0, count).Select(i => $"t{i}"));
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT t0.id FROM " + from).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        int probes = 0;
        SqlPlanExecutor.CostProbeCompleted = _ => probes++;
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None).ShouldBeOfType<SqlSearchedJoinPlan>();

            // Assert
            selected.States.ShouldBe(states);
            selected.Partitions.ShouldBe(partitions);
            probes.ShouldBe(count);
        }
        finally
        {
            SqlPlanExecutor.CostProbeCompleted = null;
            await database.Coordinator.RollbackAsync(transaction, CancellationToken.None);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join DP: every ordered partition is visited once and subsets are memoized (#1629)")]
    [InlineData(2, 3, 2)]
    [InlineData(4, 15, 50)]
    [InlineData(6, 63, 602)]
    [InlineData(8, 255, 6050)]
    public void Find_RelationCounts_ShouldBoundMemoizedWork(int count, int states, int partitions)
    {
        // Arrange
        var observed = new HashSet<(int, int)>();
        int rowCalls = 0;

        // Act
        var result = SqlJoinSearch.Find(count, mask => { rowCalls++; return 1; }, (left, right) =>
        {
            observed.Add((left, right)).ShouldBeTrue();
            (left & right).ShouldBe(0);
            return (new(0, SqlJoinType.Cross, null, null), 1);
        }, CancellationToken.None);

        // Assert
        result.States.ShouldBe(states);
        result.Partitions.ShouldBe(partitions);
        observed.Count.ShouldBe(partitions);
        rowCalls.ShouldBe(count);
        result.Tree.Leaves().ShouldBe(Enumerable.Range(0, count));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join DP: exact bushy optimum matches exhaustive binary-tree enumeration (#1629)")]
    public void Find_AllFourRelationTrees_ShouldMatchExhaustiveOracle()
    {
        // Arrange: the two disconnected pairs each need to form before the cheap root.
        double Work(int left, int right) => (left | right) is 5 or 10 ? 1 : left is 5 or 10 && right is 5 or 10 ? 1 : 100;
        double Exhaustive(int mask)
        {
            if (BitOperations.IsPow2(mask)) { return 1; }
            double best = double.PositiveInfinity;
            for (int left = (mask - 1) & mask; left != 0; left = (left - 1) & mask)
            { best = Math.Min(best, Exhaustive(left) + Exhaustive(mask ^ left) + Work(left, mask ^ left)); }
            return best;
        }

        // Act
        var result = SqlJoinSearch.Find(4, _ => 1, (left, right) => (new(0, SqlJoinType.Inner, null, null), Work(left, right)), CancellationToken.None);

        // Assert
        result.Cost.ShouldBe(Exhaustive(15));
        result.Cost.ShouldBe(7);
        result.Tree.Left!.Step.ShouldNotBeNull();
        result.Tree.Right!.Step.ShouldNotBeNull();
    }

    public static IEnumerable<object[]> OracleCases()
    {
        string[] from =
        [
            "a JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
            "a,b,c,d WHERE a.k=c.k AND b.k=d.k AND c.k=d.k",
            "a CROSS JOIN b CROSS JOIN c CROSS JOIN d WHERE a.id=b.id AND c.id=d.id",
            "a JOIN b ON a.k<b.k JOIN c ON b.k>=c.k JOIN d ON c.k<>d.k",
            "a JOIN b ON a.k=b.k AND a.v=b.v JOIN c ON b.k=c.k AND a.v=c.v JOIN d ON c.k=d.k",
            "a JOIN (b JOIN c ON b.k=c.k) ON a.k=b.k JOIN d ON c.k=d.k",
            "a JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k AND c.v=1 JOIN d ON a.k=d.k",
            "a LEFT JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k WHERE b.v=1",
            "a RIGHT JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
            "a FULL JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
            "a JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k WHERE EXISTS(SELECT 1 FROM b x WHERE x.k=a.k)",
            "a JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k WHERE NOT EXISTS(SELECT 1 FROM b x WHERE x.k=a.k)",
            "a CROSS JOIN (SELECT k FROM b) b JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
            "a LEFT JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k JOIN d ON a.k=d.k WHERE b.k IS NULL",
            "a JOIN b ON a.k=b.k JOIN c ON b.k=c.k LEFT JOIN d ON c.k=d.k WHERE d.v IS NULL",
            "a LEFT JOIN (b JOIN c ON b.k=c.k) ON a.k=b.k JOIN d ON a.k=d.k",
            "a JOIN (b LEFT JOIN c ON b.k=c.k) ON a.k=b.k JOIN d ON a.k=d.k",
            "a JOIN b ON a.k=b.k JOIN (c LEFT JOIN d ON c.k=d.k) ON b.k=c.k",
            "a RIGHT JOIN (b JOIN c ON b.k=c.k) ON a.k=b.k JOIN d ON b.k=d.k",
            "a JOIN b ON a.k=b.k FULL JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
            "a JOIN b ON a.k=b.k LEFT JOIN c ON b.k=c.k AND c.v=1 JOIN d ON a.k=d.k WHERE c.k IS NULL",
            "a JOIN b ON a.k=b.k JOIN c ON b.k=c.k JOIN d ON c.k=d.k WHERE a.id IN(SELECT id FROM b)",
            "a LEFT JOIN b USING(k) JOIN c ON b.k=c.k JOIN d ON c.k=d.k",
        ];
        for (int seed = 0; seed < 4; seed++)
        { for (int i = 0; i < from.Length; i++) { yield return [seed, from[i], i < 6]; } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: LATERAL retains the base dialect refusal (#1629)")]
    public void Plan_LateralSource_ShouldRetainUnsupportedDiagnostic()
    {
        // Arrange / Act
        var failure = Should.Throw<DatabaseParseException>(() => SqlQueryRequest.FromSql("SELECT a.id FROM a CROSS JOIN LATERAL (SELECT a.id) b"));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: DML source search fixes the target boundary and matches written execution (#1629)")]
    [InlineData("UPDATE t SET v=c.v FROM a JOIN c ON a.id=c.id WHERE t.id=c.id", false)]
    [InlineData("DELETE FROM t USING a JOIN c ON a.id=c.id WHERE t.id=c.id", false)]
    [InlineData("UPDATE t SET v=c.v FROM a JOIN c ON a.id=c.id AND c.id=1 WHERE t.v=11", true)]
    [InlineData("DELETE FROM t USING a JOIN c ON a.id=c.id AND c.id=1 WHERE t.v=11", true)]
    [InlineData("MERGE INTO t USING a JOIN c ON a.id=c.id ON t.id=c.id WHEN MATCHED THEN UPDATE SET v=c.v WHEN NOT MATCHED THEN INSERT(id,v) VALUES(c.id,c.v) WHEN NOT MATCHED BY SOURCE THEN DELETE", true)]
    [InlineData("UPDATE t SET v=c.v FROM a JOIN b ON a.id=b.id JOIN c ON b.id=c.id WHERE t.id=c.id", false)]
    [InlineData("DELETE FROM t USING a JOIN b ON a.id=b.id JOIN c ON b.id=c.id WHERE t.id=c.id", false)]
    [InlineData("MERGE INTO t USING a JOIN b ON a.id=b.id JOIN c ON b.id=c.id ON t.id=c.id WHEN MATCHED THEN UPDATE SET v=c.v", true)]
    [InlineData("MERGE INTO t USING a JOIN b ON a.id=b.id JOIN c ON b.id=c.id ON t.id=c.id WHEN MATCHED THEN UPDATE SET v=c.v WHEN NOT MATCHED THEN INSERT(id,v) VALUES(c.id,c.v) WHEN NOT MATCHED BY SOURCE THEN DELETE", true)]
    public async Task ExecuteAsync_DmlSources_ShouldMatchWrittenOracle(string sql, bool search)
    {
        // Arrange
        var results = new List<string[]>();
        for (int round = 0; round < 2; round++)
        {
            await using var engine = SqlDatabaseEngine.Create("join-dml-oracle", new());
            var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            foreach (string table in new[] { "t", "a", "b", "c" })
            {
                await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,v INT)", cancellationToken: CancellationToken.None);
                string values = table == "t" ? "(1,11),(3,33)" : table == "c" ? "(1,101),(2,202)" : "(1,11),(2,22)";
                await session.ExecuteAsync($"INSERT INTO {table} VALUES{values}", cancellationToken: CancellationToken.None);
            }
            var written = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
            var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            try
            {
                var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
                var context = new SqlStatementContext(transaction, database.Coordinator);

                // Act
                var selected = round == 0 ? written : executor.Cost(written, context, CancellationToken.None);
                if (round == 1)
                {
                    var source = selected switch { SqlUpdatePlan p => p.Source!, SqlDeletePlan p => p.Source!, SqlMergePlan p => p.Source, _ => throw new InvalidOperationException() };
                    if (search)
                    {
                        var join = source.Associations.ShouldBeOfType<SqlSearchedJoinPlan>();
                        join.SourceOnly.ShouldBeTrue();
                        if (join.Tree is { } tree) { (tree.Left!.Binding == 0 || tree.Right!.Binding == 0).ShouldBeTrue(); }
                        else { join.FirstBinding.ShouldBe(0); }
                    }
                    else
                    {
                        var join = source.Associations.ShouldBeOfType<SqlJoinPlan>();
                        join.FirstBinding.ShouldBe(0);
                        join.Steps.Select(step => step.Binding).ShouldBe(Enumerable.Range(1, join.Bindings.Count - 1));
                    }
                }
                _ = await executor.ExecuteAsync(selected, context, CancellationToken.None);
                var query = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT id,v FROM t ORDER BY id").Statement.SqlExpression);
                results.Add(await RowsAsync(executor, query, context));
            }
            finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
        }

        // Assert
        results[1].ShouldBe(results[0]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: volatile, throwing and subquery predicates stay at written levels (#1629)")]
    [InlineData("JOIN b ON observe(a.id)=b.id JOIN c ON b.id=c.id")]
    [InlineData("JOIN b ON a.id=b.id JOIN c ON b.id=c.id WHERE observe(a.id)>0")]
    [InlineData("JOIN b ON a.id=b.id JOIN c ON b.id=c.id WHERE a.id/(b.id-c.id)>0")]
    public async Task Cost_UnsafePredicates_ShouldKeepWrittenLevels(string from)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("join-unsafe");
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) => value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c" })
        { await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY)", cancellationToken: CancellationToken.None); }
        var written = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(SqlQueryRequest.FromSql("SELECT a.id FROM a " + from).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None).ShouldBeOfType<SqlJoinPlan>();

            // Assert
            selected.Steps.Select(step => step.Condition).ShouldBe(written.Steps.Select(step => step.Condition));
            selected.Where.ShouldBeSameAs(written.Where);
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: generated NULL/duplicate schemas match forced written order across legal and barrier forms (#1629)")]
    [MemberData(nameof(OracleCases))]
    public async Task ExecuteAsync_GeneratedJoinGraph_ShouldMatchWrittenOracle(int seed, string from, bool eligible)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-order-oracle", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        int relation = 0;
        foreach (string table in new[] { "a", "b", "c", "d" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k {(seed % 2 == 0 ? "INT" : "BIGINT")},v {(seed == 3 ? "DECIMAL" : "INT")})", cancellationToken: CancellationToken.None);
            var random = new Random(seed * 17 + relation++);
            string values = string.Join(',', Enumerable.Range(1, 3 + relation).Select(id =>
                $"({id},{(random.Next(4) == 0 ? "NULL" : random.Next(3).ToString())},{random.Next(2)})"));
            await session.ExecuteAsync($"INSERT INTO {table} VALUES {values}", cancellationToken: CancellationToken.None);
            if (seed >= 2) { await session.ExecuteAsync($"CREATE INDEX key_{table} ON {table}(k)", cancellationToken: CancellationToken.None); }
        }
        var request = SqlQueryRequest.FromSql("SELECT a.id,b.k,c.id,d.id FROM " + from);
        var written = new SqlPlanner(database.Catalog, null, database.Definitions.Functions).Plan(request.Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None);
            var forced = written is SqlJoinPlan { Tree: null } flat ? flat with
            {
                FirstBinding = 0,
                Steps = flat.Steps.Select(step => step with { Access = null, Hash = null }).ToArray(),
            } : written;
            var expected = await RowsAsync(executor, forced, context);
            var actual = await RowsAsync(executor, selected, context);

            // Assert: full multisets retain duplicates; no ordering clause is implied.
            actual.ShouldBe(expected);
            if (eligible) { selected.ShouldBeOfType<SqlSearchedJoinPlan>(); }
            else { selected.ShouldNotBeOfType<SqlSearchedJoinPlan>(); }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Join search: later selective input wins with stable plans and fewer candidate pairs (#1629)")]
    public async Task Cost_SkewedThirdInput_ShouldReduceWorkDeterministically()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-order-skew", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT)", cancellationToken: CancellationToken.None);
            int count = table == "c" ? 1 : 80;
            await session.ExecuteAsync($"INSERT INTO {table} VALUES " + string.Join(',', Enumerable.Range(1, count).Select(id => $"({id},{id})")), cancellationToken: CancellationToken.None);
        }
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT a.id,b.id,c.id FROM a CROSS JOIN b JOIN c ON a.k=c.k AND b.k=c.k").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act
            var selected = executor.Cost(written, context, CancellationToken.None).ShouldBeOfType<SqlSearchedJoinPlan>();
            var expected = await RowsAsync(executor, written, context);
            long before = context.Metrics.JoinCandidatePairs;
            var next = new SqlStatementContext(transaction, database.Coordinator);
            var actual = await RowsAsync(executor, selected, next);

            // Assert
            actual.ShouldBe(expected);
            selected.Reordered.ShouldBeTrue();
            before.ShouldBe(12800);
            next.Metrics.JoinCandidatePairs.ShouldBe(160);
            for (int i = 0; i < 5; i++)
            {
                var repeated = executor.Cost(written, context, CancellationToken.None).ShouldBeOfType<SqlSearchedJoinPlan>();
                repeated.ChosenOrder.ShouldBe(selected.ChosenOrder);
                repeated.SearchCost.ShouldBe(selected.SearchCost);
            }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: noncontiguous bushy subtrees retain every physical column and local seek filter (#1629)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_NoncontiguousBushyTree_ShouldMatchWrittenOracle(bool indexed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-bushy-frames", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (string table in new[] { "a", "b", "c", "d" })
        {
            await session.ExecuteAsync($"CREATE TABLE {table}(id INT PRIMARY KEY,k INT,v INT)", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync($"INSERT INTO {table} VALUES(1,1,1),(2,1,0),(3,2,1),(4,NULL,1)", cancellationToken: CancellationToken.None);
            if (indexed) { await session.ExecuteAsync($"CREATE INDEX key_{table} ON {table}(k)", cancellationToken: CancellationToken.None); }
        }
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "SELECT a.id,b.id,c.id,d.id FROM a,b,c,d WHERE a.k=c.k AND b.k=d.k AND a.v=b.v AND d.v=1").Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var terms = written.Where.ShouldBeOfType<SqlBoundLogical>().Operands;
        var left = new SqlJoinStep(2, SqlJoinType.Inner, terms[0], null)
        { Hash = SqlPlanner.HashKeys(terms[0], written.Bindings, written.Columns, 2, fullFrame: true, innerMask: 4) };
        var right = new SqlJoinStep(3, SqlJoinType.Inner, terms[1], null)
        { Hash = SqlPlanner.HashKeys(terms[1], written.Bindings, written.Columns, 3, fullFrame: true, innerMask: 8) };
        if (indexed)
        {
            var index = database.Catalog.GetIndexes(written.Bindings[3].Source.Table!.ObjectId).Single(index => index.Name == "key_d");
            right = right with { Hash = null, Access = new(3, index, [written.Bindings[1].Offset + 1], [written.Columns[written.Bindings[3].Offset + 1]]) };
        }
        var root = new SqlJoinStep(1, SqlJoinType.Inner, terms[2], null)
        { Hash = SqlPlanner.HashKeys(terms[2], written.Bindings, written.Columns, 1, fullFrame: true, innerMask: 10) };
        var tree = new SqlJoinTree(-1, new(-1, new(0), new(2), left), new(-1, new(1), new(3), right), root);
        var searched = new SqlSearchedJoinPlan(written with { Tree = tree, Steps = [left, right, root], Where = null }, 15, 50, 0, "((a,c),(b,d))", true, false);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);

            // Act
            var expected = await RowsAsync(executor, written, new(transaction, database.Coordinator));
            var actual = await RowsAsync(executor, searched, new(transaction, database.Coordinator));

            // Assert
            actual.ShouldBe(expected);
            actual.ShouldNotBeEmpty();
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Join search: above eight relations retains the bound order for SQ-Z2 (#1629)")]
    [InlineData(9)]
    [InlineData(16)]
    public async Task Cost_AboveExactLimit_ShouldKeepBoundOrder(int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("join-order-limit", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        string from = string.Join(" CROSS JOIN ", Enumerable.Range(0, count).Select(i => $"t r{i}"));
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql("SELECT r0.id FROM " + from).Statement.SqlExpression).ShouldBeOfType<SqlJoinPlan>();
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        try
        {
            // Act
            var selected = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions)
                .Cost(written, new(transaction, database.Coordinator), CancellationToken.None).ShouldBeOfType<SqlJoinPlan>();

            // Assert
            selected.Steps.Select(step => step.Binding).ShouldBe(Enumerable.Range(1, count - 1));
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }

    private static async Task<string[]> RowsAsync(SqlPlanExecutor executor, SqlPlan plan, SqlStatementContext context)
    {
        await using var result = (DatabaseResultSet)await executor.ExecuteAsync(plan, context, CancellationToken.None);
        var rows = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(string.Join('|', Enumerable.Range(0, result.Columns.Count).Select(i => row.IsNull(i) ? "NULL" : Convert.ToString(row.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)))); }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
