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

public sealed partial class SqlSelectRetentionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: static eligibility is inspected once across correlated window copies (#1776)")]
    [InlineData("SELECT k,(SELECT SUM(id+1) FROM u WHERE u.k=t.k LIMIT 1) FROM t", 2)]
    [InlineData("SELECT k,EXISTS(SELECT a.id+1 FROM u a JOIN u b ON a.k=b.k WHERE a.k=t.k LIMIT 1 OFFSET 1) FROM t", 1)]
    public async Task ExecuteAsync_CorrelatedEligibility_ShouldInspectOnce(string sql, int expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("retention-cached-inspection", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE u(id INT,k INT)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(",", Enumerable.Repeat("(1)", 128)));
        await Execute(session, "INSERT INTO u VALUES(1,1),(2,1),(3,1),(4,1)");
        int inspections = 0, invocations = 0;
        database.Definitions.ObserveSelectRetention = (name, pipeline, _) =>
        {
            if (name == "eligibility-inspection") { inspections++; }
            if (pipeline && name == (expected == 2 ? "group-input" : "join-matches")) { invocations++; }
        };

        // Act
        var rows = await Rows(session, sql);

        // Assert: each LIMIT/OFFSET execution copy uses the same static plan key.
        rows.Length.ShouldBe(128);
        invocations.ShouldBe(128);
        inspections.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: NEXT VALUE FOR beneath EXISTS keeps the reference refusal (#1776)")]
    public async Task ExecuteAsync_ExistsNextValue_ShouldKeepReferenceRefusal()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("retention-exists-next", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE SEQUENCE s");
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(3)");
        foreach (bool forceBase in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");

            // Act / Assert
            var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "SELECT EXISTS(SELECT NEXT VALUE FOR s FROM t)"));
            failure.Diagnostics.ShouldContain(item => item.Code == "CS03076");
            (await Rows(session, "SELECT nextval('s')")).ShouldBe([[1L]]);
            (await Rows(session, "SELECT EXISTS(SELECT nextval('s') FROM t)")).ShouldBe([[true]]);
            (await Rows(session, "SELECT nextval('s')")).ShouldBe([[5L]]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: correlated DML shares independent derived sequence calls (#1776)")]
    [InlineData("UPDATE t SET x=1 WHERE (SELECT MAX(d.n) FROM (SELECT nextval('s') AS n FROM u) d WHERE t.k>0) > 0")]
    [InlineData("DELETE FROM t WHERE EXISTS(SELECT 1 FROM (SELECT nextval('s') AS n FROM u) d WHERE t.k>0)")]
    public async Task ExecuteAsync_CorrelatedDmlDerived_ShouldEvaluateOnce(string sql)
    {
        // Arrange / Act: a fresh database lets both paths start from the same rows and sequence.
        foreach (bool forceBase in new[] { true, false })
        {
            await using var engine = SqlDatabaseEngine.Create("retention-correlated-dml", new());
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await Execute(session, "CREATE SEQUENCE s");
            await Execute(session, "CREATE TABLE t(k INT,x INT)");
            await Execute(session, "CREATE TABLE u(n INT)");
            await Execute(session, "INSERT INTO t VALUES(1,0),(2,0),(3,0)");
            await Execute(session, "INSERT INTO u VALUES(1)");
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

            // Assert: all targets change, while the independent derived body runs once.
            result.AffectedCount.ShouldBe(3);
            (await Rows(session, "SELECT nextval('s')")).ShouldBe([[2L]]);
            (await Rows(session, "SELECT x FROM t ORDER BY k")).ShouldBe(sql.StartsWith("DELETE", StringComparison.Ordinal)
                ? [] : [[1], [1], [1]]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: grouped correlated ordering at LIMIT zero evaluates every volatile group output (#1776)")]
    public async Task ExecuteAsync_GroupedCorrelatedLimitZero_ShouldCallEachGroup()
    {
        // Arrange
        var calls = new List<long>();
        var builder = SqlDatabaseEngine.CreateBuilder("retention-grouped-zero");
        builder.Functions.Add(SqlScalarFunction.Create("record_call", (long value) => { calls.Add(value); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE u(v INT)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(1),(3)");
        await Execute(session, "INSERT INTO u VALUES(1)");
        foreach (bool forceBase in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            calls.Clear();

            // Act / Assert
            (await Rows(session, "SELECT record_call(k) FROM t GROUP BY k ORDER BY (SELECT v FROM u WHERE t.k>0) LIMIT 0"))
                .ShouldBeEmpty();
            calls.ShouldBe([1L, 2L, 3L]);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SELECT retention: CTAS DISTINCT charges kept rows on both retention paths (#1776)")]
    public async Task ExecuteAsync_TableAsDistinctBoundary_ShouldChargeKeptRows()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("retention-table-as");
        builder.Options.StatementMemoryBudget = 4096;
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(",", Enumerable.Repeat("(1)", 1000)));
        foreach (bool forceBase in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;

            // Act
            await Execute(session, "CREATE TABLE d AS SELECT DISTINCT k FROM t");

            // Assert
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
            (await Rows(session, "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='d'")).ShouldBe([[1L]]);
            await Execute(session, "DROP TABLE d");
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: join consumer stack guards drain all base input work (#1776)")]
    [InlineData("SELECT stack_guard(t.k) FROM t CROSS JOIN u WHERE nextval('s')>0", 4L, false)]
    [InlineData("SELECT stack_guard(t.k) FROM t CROSS JOIN u WHERE nextval('s')>0 ORDER BY t.k LIMIT 1", 4L, false)]
    [InlineData("SELECT a.k FROM t a RIGHT JOIN u b ON record_call(a.k)=b.k WHERE stack_guard(b.k)>0", 1L, true)]
    public async Task ExecuteAsync_JoinConsumerStackFailure_ShouldDrainInput(string sql, long nextValue, bool observesOn)
    {
        // Arrange: inject the exact stack-guard exception without platform-dependent stack exhaustion.
        var calls = new List<long>();
        var observed = new List<(string Operator, bool Pipeline)>();
        var builder = SqlDatabaseEngine.CreateBuilder("retention-stack-drain");
        builder.Functions.Add(SqlScalarFunction.Create("record_call", (long value) => { calls.Add(value); return value; }));
        builder.Functions.Add(SqlScalarFunction.Create<long, long>("stack_guard", _ => throw new InsufficientExecutionStackException(),
            volatility: SqlFunctionVolatility.Stable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE SEQUENCE s");
        await Execute(session, "CREATE TABLE t(k INT)");
        await Execute(session, "CREATE TABLE u(k INT)");
        await Execute(session, "INSERT INTO t VALUES(1),(2),(3)");
        await Execute(session, "INSERT INTO u VALUES(1)");
        database.Definitions.ObserveSelectRetention = (name, pipeline, _) => observed.Add((name, pipeline));
        long? reads = null, pairs = null;
        foreach (bool forceBase in new[] { true, false })
        {
            database.Definitions.ForceSelectRetentionBasePath = forceBase;
            await Execute(session, "ALTER SEQUENCE s RESTART WITH 1");

            calls.Clear();
            observed.Clear();

            // Act
            var failure = await Should.ThrowAsync<DatabaseException>(() => Execute(session, sql));

            // Assert
            failure.Diagnostics.ShouldContain(item => item.Code == "CS05004");
            var metrics = session.LastStatementMetrics!;
            reads ??= metrics.RecordsExamined;
            pairs ??= metrics.JoinCandidatePairs;
            metrics.RecordsExamined.ShouldBe(reads.Value);
            metrics.JoinCandidatePairs.ShouldBe(pairs.Value);
            if (!forceBase) { observed.ShouldContain(("join-matches", true)); }
            calls.ShouldBe(observesOn ? new[] { 1L, 2L, 3L } : Array.Empty<long>());
            (await Rows(session, "SELECT nextval('s')")).ShouldBe([[nextValue]]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - SELECT retention: parenthesized corpus joins execute searched normalized slot copies (#1776)")]
    [InlineData("SELECT a.id,b.id,c.id FROM t a JOIN (u b JOIN t c ON c.k=b.k) ON a.k=b.k WHERE b.id=2 LIMIT 3", "join-matches")]
    [InlineData("SELECT COUNT(*) FROM t a JOIN (u b JOIN t c ON c.k=b.k) ON a.k=b.k WHERE b.id=2", "group-input")]
    public async Task ExecuteAsync_ParenthesizedCorpusJoin_ShouldUseSearchedTree(string sql, string pipeline)
    {
        // Arrange: match the corpus's nine rows and selective middle binding.
        await using var engine = SqlDatabaseEngine.Create("retention-searched-tree", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT)");
        await Execute(session, "CREATE TABLE u(id INT PRIMARY KEY,k INT)");
        await Execute(session, "INSERT INTO u VALUES(1,1),(2,2),(4,4)");
        foreach (int id in Enumerable.Range(1, 9).OrderBy(id => id * 7 % 11))
        { await Execute(session, $"INSERT INTO t VALUES({id},{(id % 3 == 0 ? "NULL" : (id % 3).ToString(System.Globalization.CultureInfo.InvariantCulture))})"); }
        var written = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var observed = new List<(string Operator, bool Pipeline)>();
        database.Definitions.ObserveSelectRetention = (name, streamed, _) => observed.Add((name, streamed));
        try
        {
            var executor = new SqlPlanExecutor(database.DataStorage, database.Catalog, database.IndexManager, database.Definitions);
            var context = new SqlStatementContext(transaction, database.Coordinator);

            // Act: execute the same costed plan whose physical branch is asserted below.
            var selected = executor.Cost(written, context, CancellationToken.None);
            var join = (selected is SqlGroupPlan group ? group.Input : selected).ShouldBeOfType<SqlSearchedJoinPlan>();
            var tree = join.Tree.ShouldNotBeNull();
            tree.Step.ShouldNotBeNull().Access.ShouldBeNull();
            tree.Right.ShouldNotBeNull().Step.ShouldNotBeNull();
            await using var result = (DatabaseResultSet)await executor.ExecuteAsync(selected, context, CancellationToken.None);
            var rows = new List<object?[]>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }

            // Assert: the root has a non-seek right subtree and produces rows, reaching the
            // SqlSearchedJoinPlan slot-copy branch in EnumerateNormalizedJoinRowsAsync.
            observed.ShouldContain((pipeline, true));
            if (pipeline == "group-input") { rows.ShouldBe(new object?[][] { [9L] }); }
            else
            {
                rows.Count.ShouldBe(3);
                rows.ShouldAllBe(row => (int)row[1]! == 2 && (int)row[0]! % 3 == 2 && (int)row[2]! % 3 == 2);
            }
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None); }
    }
}
