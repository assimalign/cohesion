using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlSubqueryExecutionTests
{
    private readonly ITestOutputHelper _correlationOutput;

    public SqlSubqueryExecutionTests(ITestOutputHelper output) { _correlationOutput = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: ten thousand outer rows return correct EXISTS results")]
    public async Task Execute_TenThousandCorrelatedExistsRows_ShouldReturnMatchingRows()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlation-cost");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE outer_rows (id INT, k INT);");
        await ExecuteAsync(session, "CREATE TABLE inner_rows (k INT);");
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (1);");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES " + string.Join(",", Enumerable.Range(0, 10_000).Select(i => $"({i}, {i % 2})")) + ";");
        const string sql = "SELECT o.id FROM outer_rows o WHERE EXISTS (SELECT i.k FROM inner_rows i WHERE i.k = o.k);";
        await RowsAsync(session, sql);

        // Act
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        var rows = await RowsAsync(session, sql);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        // Assert
        rows.Count.ShouldBe(5_000);
        rows.ShouldAllBe(row => (int)row[0]! % 2 == 1);
        _correlationOutput.WriteLine($"10,000-row correlated EXISTS: {stopwatch.Elapsed.TotalMilliseconds:F3} ms; {allocated:N0} total allocated bytes (process-wide).");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: outer references propagate through thirty two nested child scopes")]
    public async Task Execute_DepthThirtyTwoCorrelation_ShouldResolveOutermostFrame()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlation-depth");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);
        string child = "SELECT id FROM choices WHERE id = c.id";
        for (int depth = 1; depth < 32; depth++) { child = $"SELECT id FROM choices WHERE id IS NOT NULL AND EXISTS ({child})"; }

        // Act
        var rows = await RowsAsync(session, $"SELECT c.id FROM candidates c WHERE EXISTS ({child});");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: scalar existence and membership are scoped to each nullable repeated outer key")]
    public async Task Execute_CorrelatedForms_ShouldPreservePerRowNullAndEmptySemantics()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE outer_rows (id INT, k INT);");
        await ExecuteAsync(session, "CREATE TABLE inner_rows (k INT, value INT);");
        await ExecuteAsync(session, "INSERT INTO outer_rows VALUES (1, 1), (2, 2), (3, 3), (4, NULL), (5, 1);");
        await ExecuteAsync(session, "INSERT INTO inner_rows VALUES (1, 1), (2, NULL), (NULL, 9);");

        // Act
        var rows = await RowsAsync(session, "SELECT o.id, (SELECT i.value FROM inner_rows i WHERE i.k = o.k), " +
            "EXISTS (SELECT i.value FROM inner_rows i WHERE i.k = o.k), " +
            "NOT EXISTS (SELECT i.value FROM inner_rows i WHERE i.k = o.k), " +
            "o.k IN (SELECT i.value FROM inner_rows i WHERE i.k = o.k), " +
            "o.k NOT IN (SELECT i.value FROM inner_rows i WHERE i.k = o.k) FROM outer_rows o ORDER BY o.id;");

        // Assert
        rows.Count.ShouldBe(5);
        rows[0].ShouldBe(new object?[] { 1, 1, true, false, true, false });
        rows[1].ShouldBe(new object?[] { 2, null, true, false, null, null });
        rows[2].ShouldBe(new object?[] { 3, null, false, true, false, true });
        rows[3].ShouldBe(new object?[] { 4, null, false, true, false, true });
        rows[4].ShouldBe(new object?[] { 5, 1, true, false, true, false });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: nested frames resolve two scopes out and local names shadow outer names")]
    public async Task Execute_NestedCorrelation_ShouldResolveInnermostFirst()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, "SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x " +
            "WHERE EXISTS (SELECT z.id FROM choices z WHERE z.id = c.id)) ORDER BY c.id;"))
            .ShouldHaveSingleItem()[0].ShouldBe(2);
        (await RowsAsync(session, "SELECT c.id, (SELECT c.id FROM choices c WHERE c.id IS NOT NULL) " +
            "FROM candidates c WHERE c.id = 1;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 2 });
        (await RowsAsync(session, "SELECT c.id, (SELECT x.id FROM choices x WHERE x.id = " +
            "(SELECT c.id FROM choices z WHERE z.id IS NOT NULL)) FROM candidates c WHERE c.id IS NOT NULL ORDER BY c.id;"))
            .Select(row => row[1]).ShouldBe(new object?[] { null, 2, null });
        (await RowsAsync(session, "SELECT c.id, (SELECT (SELECT y.id FROM choices y WHERE y.id = x.id) " +
            "FROM choices x WHERE x.id = c.id GROUP BY c.id, x.id) FROM candidates c WHERE c.id IS NOT NULL ORDER BY c.id;"))
            .Select(row => row[1]).ShouldBe(new object?[] { null, 2, null });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: ON WHERE HAVING and projection compose with completed grouping keys")]
    public async Task Execute_CorrelatedClauses_ShouldUseTheirOwnRowsAndGroupKeys()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE sales (id INT, k INT, amount INT);");
        await ExecuteAsync(session, "CREATE TABLE labels (k INT, threshold INT);");
        await ExecuteAsync(session, "INSERT INTO sales VALUES (1, 1, 4), (2, 1, 6), (3, 2, 8), (4, 3, 3);");
        await ExecuteAsync(session, "INSERT INTO labels VALUES (1, 9), (2, 10);");

        // Act / Assert
        (await RowsAsync(session, "SELECT s.id, l.k FROM sales s LEFT JOIN labels l " +
            "ON s.k = l.k AND EXISTS (SELECT x.k FROM labels x WHERE x.k = s.k AND x.k = l.k) " +
            "WHERE EXISTS (SELECT x.k FROM labels x WHERE x.k = s.k) ORDER BY s.id;"))
            .Select(row => row[1]).ShouldBe(new object?[] { 1, 1, 2 });
        (await RowsAsync(session, "SELECT s.k, SUM(s.amount), (SELECT l.threshold FROM labels l WHERE l.k = s.k) " +
            "FROM sales s GROUP BY s.k HAVING SUM(s.amount) > (SELECT l.threshold FROM labels l WHERE l.k = s.k) ORDER BY s.k;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 1, 10m, 9 });
        (await RowsAsync(session, "SELECT s.id FROM sales s ORDER BY (SELECT l.threshold FROM labels l WHERE l.k = s.k) DESC, s.id;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 3, 1, 2, 4 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: invalid grouped outer names ambiguous scopes and outer-only aggregates fail during binding")]
    [InlineData("SELECT (SELECT x.id FROM choices x WHERE x.id = c.id) FROM candidates c GROUP BY c.id + 1;", "COHSQLE017", "Outer column 'id'")]
    [InlineData("SELECT (SELECT SUM(c.id) FROM choices x) FROM candidates c;", "CK00001", "outer")]
    [InlineData("SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x WHERE SUM(c.id) > 0);", "CK00001", "outer")]
    [InlineData("SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x JOIN choices y ON SUM(c.id) > 0);", "CK00001", "outer")]
    [InlineData("SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.id FROM choices x JOIN candidates y ON x.id = y.id WHERE id = c.id);", "COHSQLE015", "Ambiguous")]
    [InlineData("SELECT c.id FROM candidates c WHERE EXISTS (SELECT x.missing FROM choices x WHERE x.missing = c.id);", "COHSQLE015", "x.missing")]
    public async Task Execute_InvalidCorrelatedBinding_ShouldReportNamedDiagnostic(string sql, string code, string name)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act
        if (code == "CK00001")
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            // Assert
            result.Status.ShouldBe(DatabaseResultStatus.Error);
            result.Diagnostics.ShouldNotBeNull().ShouldContain(diagnostic => diagnostic.Code == code && diagnostic.Message!.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var error = await Should.ThrowAsync<SqlPlanningException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
            // Assert
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code && diagnostic.Message!.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: lazy branches skip cardinality failures and a later row failure is atomic")]
    public async Task Execute_CorrelatedCardinality_ShouldFailOnlyWhenReachedAndKeepSessionUsable()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, "SELECT CASE WHEN c.id = 1 THEN 9 ELSE (SELECT x.id FROM choices x WHERE c.id = 1) END " +
            "FROM candidates c WHERE c.id = 1;" )).ShouldHaveSingleItem()[0].ShouldBe(9L);
        var error = await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session,
            "SELECT c.id, (SELECT x.id FROM choices x WHERE x.id = c.id OR c.id = 3) FROM candidates c WHERE c.id IS NOT NULL ORDER BY c.id;"));
        error.Message.ShouldContain("one row");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
            "UPDATE candidates SET id = id + 10 WHERE id = (SELECT x.id FROM choices x WHERE x.id = candidates.id OR candidates.id = 3);"));
        (await RowsAsync(session, "SELECT id FROM candidates ORDER BY id;")).Select(row => row[0])
            .ShouldBe(new object?[] { null, 1, 2, 3 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: outer-only predicates never narrow a same-named inner index")]
    [InlineData("t.id = 1", 2L)]
    [InlineData("1 = t.id", 2L)]
    [InlineData("t.id BETWEEN 1 AND 1", 2L)]
    [InlineData("t.id = 1 AND u.id = 10", 1L)]
    public async Task Execute_IndexedChildOuterPredicate_ShouldRetainAllQualifyingInnerRows(string predicate, long count)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated-index");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "CREATE TABLE u (id INT PRIMARY KEY);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2);");
        await ExecuteAsync(session, "INSERT INTO u VALUES (10), (20);");

        // Act
        var rows = await RowsAsync(session, $"SELECT t.id, (SELECT COUNT(*) FROM u WHERE {predicate}) FROM t ORDER BY t.id;");

        // Assert
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(new object?[] { 1, count });
        rows[1].ShouldBe(new object?[] { 2, 0L });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Correlation: scalar membership and existence compose in WHERE HAVING and ON")]
    [InlineData("SELECT c.id FROM candidates c WHERE c.id = (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    [InlineData("SELECT c.id FROM candidates c GROUP BY c.id HAVING c.id IN (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    [InlineData("SELECT c.id FROM candidates c GROUP BY c.id HAVING c.id = (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    [InlineData("SELECT c.id FROM candidates c GROUP BY c.id HAVING EXISTS (SELECT x.id FROM choices x WHERE x.id = c.id);")]
    [InlineData("SELECT c.id FROM candidates c JOIN choices x ON c.id = x.id AND c.id IN (SELECT z.id FROM choices z WHERE z.id = c.id);")]
    [InlineData("SELECT c.id FROM candidates c JOIN choices x ON c.id = (SELECT z.id FROM choices z WHERE z.id = c.id) AND x.id = c.id;")]
    public async Task Execute_CorrelatedClauseForms_ShouldReturnMatchingKey(string sql)
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated-clauses");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);

        // Act / Assert
        (await RowsAsync(session, sql)).ShouldHaveSingleItem()[0].ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: derived output names metadata and system predicates cross enclosing frames")]
    public async Task Execute_CorrelatedDerivedAndCatalog_ShouldRetainNamesAndTypes()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated-derived");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT, label CHAR(3) COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1, 'a'), (2, 'b');");

        // Act / Assert
        (await RowsAsync(session, "SELECT t.id FROM t WHERE EXISTS (SELECT d.id FROM (SELECT t.id FROM t local_t WHERE local_t.id = 1) d WHERE d.id = t.id) ORDER BY t.id;"))
            .Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        (await RowsAsync(session, "SELECT t.id FROM t WHERE EXISTS (SELECT d.label FROM (SELECT t.label COLLATE case_insensitive AS label FROM t local_t WHERE local_t.id = 1) d WHERE d.label = 'A') ORDER BY t.id;"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await RowsAsync(session, "SELECT t.id FROM t WHERE EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES c WHERE c.TABLE_NAME = 't' AND t.id = 1);"))
            .ShouldHaveSingleItem()[0].ShouldBe(1);
        (await ExecuteAsync(session, "DELETE FROM t WHERE EXISTS (SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES c WHERE c.TABLE_NAME = 't' AND t.id = 2);"))
            .AffectedCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Correlation: UPDATE DELETE and rollback use pre-statement and uncommitted rows")]
    public async Task Execute_CorrelatedDml_ShouldReturnExactCountsAndRollback()
    {
        // Arrange
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("correlated");
        await using var session = await database.CreateSessionAsync(cancellationToken: CancellationToken.None);
        await SeedMembershipAsync(session);
        await using var transaction = await session.BeginTransactionAsync(cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "INSERT INTO choices VALUES (3);");

        // Act / Assert
        (await ExecuteAsync(session, "UPDATE candidates SET id = id + 10 WHERE EXISTS (SELECT x.id FROM choices x WHERE x.id = candidates.id);")).AffectedCount.ShouldBe(2);
        (await ExecuteAsync(session, "DELETE FROM candidates WHERE id IN (SELECT x.id + 10 FROM choices x WHERE x.id + 10 = candidates.id);")).AffectedCount.ShouldBe(2);
        await transaction.RollbackAsync(CancellationToken.None);
        (await RowsAsync(session, "SELECT id FROM candidates ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { null, 1, 2, 3 });
    }
}
