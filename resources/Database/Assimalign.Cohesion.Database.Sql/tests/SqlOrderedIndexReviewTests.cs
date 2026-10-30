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

using static Assimalign.Cohesion.Database.Sql.Tests.SqlPartialIndexTests;
using static Assimalign.Cohesion.Database.Sql.Tests.SqlOrderedIndexTests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlOrderedIndexReviewTests
{
    private readonly ITestOutputHelper _output;

    public SqlOrderedIndexReviewTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Default order: non-NULL keys match either direction and stop at LIMIT (#1631)")]
    [InlineData("k", false)]
    [InlineData("k DESC", true)]
    [InlineData("k LIMIT 10", false)]
    [InlineData("k DESC LIMIT 10", true)]
    [InlineData("id LIMIT 10", false)]
    public async Task ExecuteAsync_NonNullableDefaultOrder_ShouldMatchForcedSort(string order, bool reverse)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-not-null", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT NOT NULL)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 1024).Select(i => $"({i},{1023-i})")));
        await Execute(session, "CREATE INDEX ix ON t(k)");
        string column = order.StartsWith("id", StringComparison.Ordinal) ? "id" : "k";
        var planned = Plan(database, "SELECT id FROM t ORDER BY " + order);

        // Act / Assert: check binding even when unbounded runtime costing prefers a scan.
        planned.Access.ShouldBeOfType<SqlOrderedIndexPath>().Reverse.ShouldBe(reverse);
        var expected = await Rows(session, "SELECT id FROM t ORDER BY " + order.Replace(column, column + "+0", StringComparison.Ordinal));
        var actual = await Rows(session, "SELECT id FROM t ORDER BY " + order);
        actual.ShouldBe(expected);
        if (order.Contains("LIMIT", StringComparison.Ordinal))
        {
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(10);
            session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        }
        _output.WriteLine($"{order}: heap={session.LastStatementMetrics!.RecordsExamined}; sort={session.LastStatementMetrics.PeakOrderedRows}");
        if (order.Contains("LIMIT", StringComparison.Ordinal))
        {
            var explanation = await Rows(session, "EXPLAIN SELECT id FROM t ORDER BY " + order);
            explanation.ShouldContain(row => Equals(row[2], "OrderedIndexScan") &&
                (row[5] as string ?? "").Contains(reverse ? "direction=backward; sort_removed=true" : "direction=forward; sort_removed=true", StringComparison.Ordinal));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NULL rejection: mandatory comparisons and predicates relax placement, OR and NOT do not (#1631)")]
    [InlineData("k>7", true)]
    [InlineData("k>id", true)]
    [InlineData("k<>7", true)]
    [InlineData("k IN(7,8,9)", true)]
    [InlineData("k BETWEEN 7 AND 90", true)]
    [InlineData("k IS NOT NULL", true)]
    [InlineData("k>7 OR id=0", false)]
    [InlineData("NOT(k>7)", false)]
    [InlineData("id>7", false)]
    [InlineData("", false)]
    public async Task ExecuteAsync_NullRejectingConjuncts_ShouldRequireMandatoryProof(string predicate, bool matches)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-null-proof", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,k INT)");
        await Execute(session, "INSERT INTO t VALUES(0,NULL)," + string.Join(',', Enumerable.Range(1, 128).Select(i => $"({i},{i})")));
        await Execute(session, "CREATE INDEX ix ON t(k)");
        string sql = "SELECT id FROM t" + (predicate.Length == 0 ? "" : " WHERE " + predicate) + " ORDER BY k{0} LIMIT 10";

        // Act / Assert
        (Plan(database, string.Format(sql, "")).Access is SqlOrderedIndexPath).ShouldBe(matches);
        var expected = await Rows(session, string.Format(sql, "+0"));
        var actual = await Rows(session, string.Format(sql, ""));
        actual.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Parameter NULL rejection: default order stops at ten and equals forced sort (#1631)")]
    public async Task ExecuteAsync_ParameterizedNullRejection_ShouldStopEarly()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-parameter-proof", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,k INT)");
        await Execute(session, "INSERT INTO t VALUES(0,NULL)," + string.Join(',', Enumerable.Range(1, 128).Select(i => $"({i},{i})")));
        await Execute(session, "CREATE INDEX ix ON t(k)");
        var parameters = new Dictionary<string, object?> { ["1"] = 7 };
        var request = SqlQueryRequest.FromSql("SELECT id FROM t WHERE k>$1 ORDER BY k LIMIT 10", parameters);
        var plan = new SqlPlanner(database.Catalog, parameters).Plan(request.Statement.SqlExpression).ShouldBeOfType<SqlSelectPlan>();

        // Act
        plan.Access.ShouldBeOfType<SqlOrderedIndexPath>();
        await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var actual = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { actual.Add(row.GetValue(0)); }

        // Assert
        actual.ShouldBe(Enumerable.Range(8, 10).Cast<object?>());
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(10);
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        var forced = SqlQueryRequest.FromSql("SELECT id FROM t WHERE k>$1 ORDER BY k+0 LIMIT 10", parameters);
        await using var sorted = (await session.ExecuteAsync(forced, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var expected = new List<object?>();
        await foreach (var row in sorted.GetRowsAsync(CancellationToken.None)) { expected.Add(row.GetValue(0)); }
        actual.ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered costing: sparse residuals retain seeks with incomplete table probes (#1631)")]
    [InlineData(20000, 50)]
    [InlineData(2000, 500)]
    public async Task ExecuteAsync_SparseResidual_ShouldChooseSeek(int modulus, int matches)
    {
        // Arrange: the review's million-row examples, inserted before index construction.
        await using var engine = SqlDatabaseEngine.Create("order-selectivity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,a INT,k INT)");
        for (int start = 0; start < 1_000_000; start += 1000)
        {
            await Execute(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, 1000).Select(i => $"({i},{i % modulus},{i})")));
        }
        await Execute(session, "CREATE INDEX ix_a ON t(a)");
        await Execute(session, "CREATE INDEX ix_k ON t(k)");

        // Act / Assert
        if (modulus == 20000)
        {
            var rows = await Rows(session, "SELECT id FROM t WHERE a=7 ORDER BY k ASC NULLS LAST LIMIT 10");
            rows.Select(row => row[0]).ShouldBe(Enumerable.Range(0, 10).Select(i => (object?)(7 + modulus * i)));
            _output.WriteLine($"modulus={modulus}; LIMIT: path={session.LastStatementMetrics!.AccessPath}; heap={session.LastStatementMetrics.RecordsExamined}");
            session.LastStatementMetrics.AccessPath.ShouldBe("seek:ix_a");
            session.LastStatementMetrics.RecordsExamined.ShouldBe(matches);
        }
        if (modulus == 2000)
        {
            var rows = await Rows(session, "SELECT id FROM t WHERE a=7 ORDER BY k ASC NULLS LAST");
            rows.Select(row => row[0]).ShouldBe(Enumerable.Range(0, matches).Select(i => (object?)(7 + modulus * i)));
            _output.WriteLine($"unbounded: path={session.LastStatementMetrics!.AccessPath}; heap={session.LastStatementMetrics.RecordsExamined}");
            session.LastStatementMetrics.AccessPath.ShouldBe("seek:ix_a");
            session.LastStatementMetrics.RecordsExamined.ShouldBe(500);
        }
        await Rows(session, "SELECT id FROM t WHERE a>=0 ORDER BY k ASC NULLS LAST LIMIT 10");
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix_k:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(10);
        _output.WriteLine("majority residual: ordered walk; heap=10");
        await Rows(session, "SELECT id FROM t ORDER BY k ASC NULLS LAST LIMIT 10");
        session.LastStatementMetrics!.AccessPath.ShouldBe("ordered:ix_k:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(10);
        _output.WriteLine("unfiltered: ordered walk; heap=10");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial selectivity: predicates enforced by the ordered tree or counted sibling are not charged again (#1631)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EnforcedPartialPredicate_ShouldKeepDenseOrderedWalk(bool orderedSubset)
    {
        // Arrange: eighty of one hundred rows qualify; the partial probe is exact.
        await using var engine = SqlDatabaseEngine.Create("order-partial-selectivity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,a INT,k INT,active BOOLEAN)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 100)
            .Select(i => $"({i},{100-i},{i},{(i < 80 ? "TRUE" : "FALSE")})")));
        if (!orderedSubset) { await Execute(session, "CREATE INDEX ix_a ON t(a) WHERE active=TRUE"); }
        await Execute(session, "CREATE INDEX ix_k ON t(k)" + (orderedSubset ? " WHERE active=TRUE" : ""));

        // Act
        var rows = await Rows(session, "SELECT id FROM t WHERE active=TRUE ORDER BY k ASC NULLS LAST LIMIT 10");

        // Assert
        rows.Select(row => row[0]).ShouldBe(Enumerable.Range(0, 10).Cast<object?>());
        _output.WriteLine($"subset={orderedSubset}; path={session.LastStatementMetrics!.AccessPath}; heap={session.LastStatementMetrics.RecordsExamined}");
        session.LastStatementMetrics.AccessPath.ShouldBe("ordered:ix_k:forward");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(10);
        session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Ordered precheck: inspect only the target table and decline before allocating candidates (#1631)")]
    [InlineData("", "CREATE INDEX ix ON t(k) WHERE a=1")]
    [InlineData(" WHERE a=b+1", "CREATE INDEX ix ON t(a,k)")]
    [InlineData(" WHERE a=1.5", "CREATE INDEX ix ON t(a,k)")]
    public async Task Plan_DecliningOrder_ShouldInspectOnlyTargetWithoutAllocating(string predicate, string ddl)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-precheck", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        for (int i = 0; i < 64; i++)
        {
            await Execute(session, $"CREATE TABLE other{i}(k INT)");
            await Execute(session, $"CREATE INDEX other_ix{i} ON other{i}(k)");
        }
        await Execute(session, "CREATE TABLE t(a INT,b INT,k INT)");
        await Execute(session, ddl);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        int inspected = 0, allocatingPasses = 0;
        long start = 0, minimum = long.MaxValue;
        var inspectedTables = new HashSet<ulong>();
        SqlPlanner.OrderedIndexInspected = index => { inspected++; inspectedTables.Add(index.TableObjectId); };
        SqlPlanner.OrderedCandidatesStarted = () => allocatingPasses++;
        SqlPlanner.OrderedPrecheck = completed =>
        {
            if (!completed) { start = GC.GetAllocatedBytesForCurrentThread(); }
            else { minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - start); }
        };
        // Warm the hooks and collection storage before the measured rounds.
        Plan(database, "SELECT k FROM t" + predicate + " ORDER BY k ASC NULLS LAST LIMIT 10");
        inspected = allocatingPasses = 0;
        minimum = long.MaxValue;
        try
        {
            // Act: allocation evidence is the minimum of three rounds.
            for (int round = 0; round < 3; round++)
            {
                Plan(database, "SELECT k FROM t" + predicate + " ORDER BY k ASC NULLS LAST LIMIT 10").Access.ShouldNotBeOfType<SqlOrderedIndexPath>();
            }

            // Assert
            _output.WriteLine($"{predicate}; inspected={inspected}; allocating passes={allocatingPasses}; precheck bytes={minimum}");
            inspectedTables.ShouldBe(new[] { table!.ObjectId }, ignoreOrder: true);
            inspected.ShouldBe(3);
            allocatingPasses.ShouldBe(0);
            minimum.ShouldBe(0);
        }
        finally { SqlPlanner.OrderedIndexInspected = null; SqlPlanner.OrderedCandidatesStarted = null; SqlPlanner.OrderedPrecheck = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Residual defaults: equality, LIKE, ranges and conjunctions debit top-N work (#1631)")]
    [InlineData("a=7")]
    [InlineData("a%3=0")]
    [InlineData("a>5 AND a<15")]
    [InlineData("5<a AND a<15")]
    [InlineData("a BETWEEN 5 AND 15")]
    [InlineData("name LIKE '7%'")]
    [InlineData("a>5 AND b>5")]
    public async Task ExecuteAsync_UnknownResidualSelectivity_ShouldChooseScan(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-default-selectivity", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT,a INT,b INT,k INT,name TEXT)");
        await Execute(session, "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(0, 200).Select(i => $"({i},{i},{i},{i},'{i}')")));
        await Execute(session, "CREATE INDEX ix ON t(k)");

        // Act
        var expected = await Rows(session, "SELECT id FROM t WHERE " + predicate + " ORDER BY k+0 ASC NULLS LAST LIMIT 10");
        var actual = await Rows(session, "SELECT id FROM t WHERE " + predicate + " ORDER BY k ASC NULLS LAST LIMIT 10");

        // Assert
        actual.ShouldBe(expected);
        session.LastStatementMetrics!.AccessPath.ShouldBe("scan");
        session.LastStatementMetrics.RecordsExamined.ShouldBe(200);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NULL proof: LIKE and proven partial predicates relax placement; outer joins retain sorting (#1631)")]
    public async Task ExecuteAsync_LocalNullProof_ShouldPreserveOuterNullExtension()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-proof-scope", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT,name TEXT)");
        await Execute(session, "INSERT INTO t VALUES(1,NULL,NULL),(2,7,'ab'),(3,8,'ac')");
        await Execute(session, "CREATE INDEX ix_name ON t(name)");
        await Execute(session, "CREATE INDEX ix_partial ON t(k) WHERE k IS NOT NULL");
        await Execute(session, "CREATE TABLE l(id INT PRIMARY KEY)");
        await Execute(session, "INSERT INTO l VALUES(1),(2),(4)");

        // Act / Assert
        Plan(database, "SELECT id FROM t WHERE name LIKE 'a%' ORDER BY name LIMIT 10").Access.ShouldBeOfType<SqlOrderedIndexPath>();
        Plan(database, "SELECT id FROM t WHERE k>0 ORDER BY k LIMIT 10").Access.ShouldBeOfType<SqlOrderedIndexPath>();
        (await Rows(session, "SELECT id FROM t WHERE name LIKE 'a%' ORDER BY name LIMIT 10"))
            .ShouldBe(await Rows(session, "SELECT id FROM t WHERE name LIKE 'a%' ORDER BY name||'' LIMIT 10"));
        (await Rows(session, "SELECT id FROM t WHERE k>0 ORDER BY k LIMIT 10"))
            .ShouldBe(await Rows(session, "SELECT id FROM t WHERE k>0 ORDER BY k+0 LIMIT 10"));
        string sql = "SELECT l.id,t.k FROM l LEFT JOIN t ON l.id=t.id AND t.k>0 ORDER BY t.k{0},l.id LIMIT 10";
        var actual = await Rows(session, string.Format(sql, ""));
        var metrics = session.LastStatementMetrics!;
        actual.ShouldBe(await Rows(session, string.Format(sql, "+0")));
        metrics.PeakOrderedRows.ShouldBe(3);
        actual.Select(row => row[0]).ShouldBe(new object?[] { 1, 4, 2 });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Nullable partial uniqueness: a NULL equality range remains a costed multi-row scan (#1631)")]
    public async Task ExecuteAsync_UniquePartialNullRange_ShouldKeepCostProbeAndAllRows()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-null-unique", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT,active BOOLEAN)");
        await Execute(session, "CREATE UNIQUE INDEX ix ON t(k) WHERE active");
        await Execute(session, "INSERT INTO t VALUES(1,NULL,TRUE),(2,NULL,TRUE),(3,NULL,TRUE)");
        int probes = 0;
        SqlPlanExecutor.CostProbeCompleted = _ => probes++;
        try
        {
            // Act
            var rows = await Rows(session, "SELECT id FROM t WHERE active AND k IS NULL ORDER BY k ASC NULLS LAST");

            // Assert: partial uniqueness does not make NULL-containing keys singletons.
            rows.Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 }, ignoreOrder: true);
            probes.ShouldBe(1);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(3);
            session.LastStatementMetrics.PeakOrderedRows.ShouldBe(0);
        }
        finally { SqlPlanExecutor.CostProbeCompleted = null; }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Ordered point lookup: unique equality retains zero cost probes (#1631)")]
    public async Task ExecuteAsync_UniqueEqualityOrder_ShouldAvoidCostProbes()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("order-point", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Execute(session, "CREATE TABLE t(id INT PRIMARY KEY)");
        await Execute(session, "INSERT INTO t VALUES(7)");
        int probes = 0;
        SqlPlanExecutor.CostProbeCompleted = _ => probes++;
        try
        {
            // Act
            await Rows(session, "SELECT id FROM t WHERE id=7 ORDER BY id ASC NULLS LAST");

            // Assert
            _output.WriteLine($"point lookup: probes={probes}; heap={session.LastStatementMetrics!.RecordsExamined}");
            probes.ShouldBe(0);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(1);
        }
        finally { SqlPlanExecutor.CostProbeCompleted = null; }
    }
}
