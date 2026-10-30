using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlCteExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive WITH: malformed references report their binding code")]
    [InlineData("SELECT n FROM r", "CS01054")]
    [InlineData("SELECT n FROM r INTERSECT SELECT 1", "CS01054")]
    [InlineData("SELECT n FROM r UNION ALL SELECT 1", "CS01055")]
    [InlineData("SELECT 1 UNION ALL SELECT (SELECT n FROM r)", "CS01056")]
    [InlineData("SELECT 1 UNION ALL SELECT 1 WHERE EXISTS (SELECT n FROM r)", "CS01056")]
    [InlineData("SELECT 1 UNION ALL SELECT 1 WHERE 1 IN (SELECT n FROM r)", "CS01056")]
    [InlineData("SELECT 1 UNION ALL SELECT 1 WHERE 1 = ANY (SELECT n FROM r)", "CS01056")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM t LEFT JOIN r ON TRUE", "CS01057")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM r RIGHT JOIN t ON TRUE", "CS01057")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM r FULL JOIN t ON TRUE", "CS01057")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM t FULL JOIN r ON TRUE", "CS01057")]
    [InlineData("SELECT 1 UNION ALL SELECT x.n FROM (r CROSS JOIN t) x RIGHT JOIN t z ON TRUE", "CS01057")]
    [InlineData("SELECT 1 UNION ALL (SELECT n FROM r INTERSECT ALL SELECT 1)", "CS01058")]
    [InlineData("SELECT 1 UNION ALL (SELECT 1 INTERSECT ALL SELECT n FROM r)", "CS01058")]
    [InlineData("SELECT 1 UNION ALL (SELECT n FROM r EXCEPT ALL SELECT 1)", "CS01059")]
    [InlineData("SELECT 1 UNION ALL (SELECT 1 EXCEPT SELECT n FROM r)", "CS01059")]
    [InlineData("SELECT 1 UNION ALL SELECT a.n FROM r a CROSS JOIN r b", "CS01060")]
    [InlineData("SELECT 1 UNION ALL (SELECT n FROM r UNION ALL SELECT n FROM r)", "CS01060")]
    [InlineData("SELECT 1 UNION ALL SELECT COUNT(*) FROM r", "CS01061")]
    [InlineData("SELECT 1 UNION ALL SELECT (SELECT SUM(r.n)) FROM r", "CS01061")]
    [InlineData("SELECT 1 UNION ALL SELECT (SELECT (SELECT SUM(r.n))) FROM r", "CS01061")]
    [InlineData("SELECT 1 UNION ALL SELECT n FROM r GROUP BY n HAVING COUNT(*) > 0", "CS01061")]
    [InlineData("SELECT 1 UNION ALL SELECT n FROM r ORDER BY n", "CK00001", "ORDER BY")]
    [InlineData("SELECT 1 UNION ALL SELECT n FROM r LIMIT 3", "CK00001", "LIMIT")]
    [InlineData("SELECT 1 UNION ALL SELECT n FROM r OFFSET 1", "CK00001", "OFFSET")]
    [InlineData("SELECT 1 UNION ALL SELECT n, n FROM r", "CS02065")]
    [InlineData("SELECT 1 UNION ALL SELECT CAST(n AS DECIMAL) FROM r", "CS02066")]
    [InlineData("SELECT CAST('x' AS VARCHAR(2)) UNION ALL SELECT CAST(n AS VARCHAR(3)) FROM r", "CS02066")]
    [InlineData("SELECT 1 UNION ALL SELECT CAST(n AS TEXT) FROM r", "CS02066")]
    [InlineData("SELECT NULL UNION ALL SELECT 1 FROM r", "CS02066")]
    [InlineData("SELECT 'x' COLLATE binary UNION ALL SELECT n COLLATE case_insensitive FROM r", "CS02067")]
    [InlineData("SELECT 'x' UNION ALL SELECT n COLLATE case_insensitive FROM r", "CS02067")]
    public async Task Execute_InvalidRecursion_ShouldFailBeforeReading(string body, string code, string? construct = null)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await RecursiveDiagnosticAsync(session,
            $"WITH RECURSIVE r(n) AS ({body}) SELECT n FROM r;");

        // Assert
        error.Code.ShouldBe(code);
        error.Start.ShouldNotBeNull();
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        if (construct is not null) { error.Message!.ShouldContain(construct, Case.Sensitive); }
        if (code == "CS02066") { error.Message!.ShouldContain("Cast the output", Case.Sensitive); }
        if (code == "CS02067") { error.Message!.ShouldContain("COLLATE", Case.Sensitive); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive WITH: valid binding creates a recursive union operator")]
    [InlineData("SELECT 1 UNION ALL SELECT n+1 FROM r WHERE n<3")]
    [InlineData("SELECT 1 UNION SELECT n FROM r")]
    [InlineData("SELECT 1 UNION ALL SELECT '3' FROM r")]
    [InlineData("SELECT 1 UNION ALL SELECT CAST(n AS INT) FROM r")]
    [InlineData("SELECT 1 UNION ALL (SELECT n FROM r INTERSECT SELECT 1)")]
    [InlineData("SELECT 1 UNION ALL (SELECT n FROM r EXCEPT SELECT 2)")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM r LEFT JOIN t ON TRUE")]
    [InlineData("SELECT 1 UNION ALL SELECT r.n FROM t RIGHT JOIN r ON TRUE")]
    [InlineData("SELECT 1 UNION ALL SELECT d.n FROM (SELECT n FROM r) d")]
    [InlineData("SELECT 1 UNION ALL SELECT n+(SELECT COUNT(*) FROM t) FROM r")]
    [InlineData("SELECT 1 UNION ALL SELECT (SELECT r.n) FROM r")]
    [InlineData("SELECT 1 UNION ALL SELECT CAST((SELECT SUM(t.id+r.n) FROM t) AS BIGINT) FROM r")]
    [InlineData("SELECT NULL UNION ALL SELECT n FROM r")]
    [InlineData("SELECT 'x' UNION ALL SELECT n FROM r")]
    [InlineData("SELECT 'x' COLLATE case_insensitive UNION ALL SELECT n FROM r")]
    [InlineData("SELECT CAST('x' AS VARCHAR(3)) UNION ALL SELECT CAST(n AS VARCHAR(3)) FROM r")]
    public async Task Plan_ValidRecursion_ShouldCreateFixpointOperator(string body)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-bound", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var query = SqlQueryRequest.FromSql($"WITH RECURSIVE r(n) AS ({body}) SELECT n FROM r;");

        // Act
        var plan = new SqlPlanner(database.Catalog, null).Plan(query.Statement.SqlExpression).ShouldBeOfType<SqlWithPlan>();

        // Assert
        plan.Relations.ShouldHaveSingleItem().Query.ShouldBeOfType<SqlRecursiveUnionPlan>();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Recursive WITH: dependency analysis and output names bind all definitions")]
    [InlineData("WITH RECURSIVE a AS (SELECT n FROM b), b AS (SELECT n FROM a) SELECT * FROM a", "CK00001")]
    [InlineData("WITH RECURSIVE a AS (SELECT n FROM b), b AS (SELECT n FROM c), c AS (SELECT n FROM a) SELECT 1", "CK00001")]
    [InlineData("WITH RECURSIVE r(n,m) AS (SELECT 1 UNION ALL SELECT n FROM r) SELECT n FROM r", "CS02065")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT missing FROM r) SELECT n FROM r", "CS01015")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT r.column1 FROM r) SELECT n FROM r", "CS01015")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r) SELECT missing FROM r", "CS01015")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r), unused AS (SELECT missing FROM t) SELECT 1", "CS01015")]
    [InlineData("WITH RECURSIVE a AS (SELECT 1), A AS (SELECT 2) SELECT 1", "CS01026")]
    [InlineData("WITH RECURSIVE a AS (SELECT (SELECT n FROM b) AS n), b AS (SELECT n FROM a) SELECT 1", "CK00001")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT 'bad' FROM r) SELECT n FROM r", "CS02022")]
    [InlineData("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n FROM r) SELECT (SELECT SUM(r.n)) FROM r", "CK00001")]
    public async Task Execute_InvalidRecursiveList_ShouldKeepBindingDiagnostic(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-list", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var error = await RecursiveDiagnosticAsync(session, sql);

        // Assert
        error.Code.ShouldBe(code);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive WITH: acyclic entries execute in dependency order and materialize once")]
    public async Task Execute_AcyclicRecursiveList_ShouldPreserveMaterialization()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-acyclic", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await RowsAsync(session, "WITH RECURSIVE b AS (SELECT id FROM a), a AS (SELECT id FROM t) SELECT x.id FROM b x JOIN a y ON x.id=y.id ORDER BY x.id");
        long examined = session.LastStatementMetrics!.RecordsExamined;
        var plan = new SqlPlanner(database.Catalog, null).Plan(SqlQueryRequest.FromSql(
            "WITH RECURSIVE b AS (SELECT id FROM a), a AS (SELECT id FROM t) SELECT * FROM b").Statement.SqlExpression).ShouldBeOfType<SqlWithPlan>();

        // Assert
        rows.Select(row => row[0]).ShouldBe([1, 2, 3]);
        examined.ShouldBe(3);
        plan.Relations.Select(relation => relation.Name).ShouldBe(["a", "b"]);
        (await RowsAsync(session, "WITH RECURSIVE t AS (SELECT id FROM dbo.t) SELECT COUNT(*) FROM t"))
            .ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive WITH: view creation freezes anchor output and executes")]
    public async Task Execute_RecursiveViewBody_ShouldBindMetadataWithoutExecution()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-view", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync("CREATE VIEW v AS WITH RECURSIVE r(label) AS (SELECT CAST('x' AS VARCHAR(3)) COLLATE case_insensitive UNION SELECT label FROM r) SELECT label FROM r;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW inferred AS WITH RECURSIVE r AS (SELECT 1 AS seed_value UNION ALL SELECT seed_value AS ignored FROM r) SELECT seed_value FROM r;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE VIEW unknown_output AS WITH RECURSIVE r AS (SELECT NULL AS n UNION ALL SELECT n FROM r) SELECT n FROM r;", cancellationToken: CancellationToken.None);
        database.Catalog.TryGetView("dbo", "v", out var view).ShouldBeTrue();
        var rows = await RowsAsync(session, "SELECT label FROM v");

        // Assert
        var column = view.Columns.ShouldHaveSingleItem();
        column.Name.ShouldBe("label");
        column.Type.Type.ShouldBe(DatabaseType.String);
        column.Type.MaxLength.ShouldBe(3);
        column.Collation.ShouldNotBeNull();
        database.Catalog.TryGetView("dbo", "inferred", out var inferred).ShouldBeTrue();
        inferred.Columns.ShouldHaveSingleItem().Name.ShouldBe("seed_value");
        inferred.Columns[0].Type.Type.ShouldBe(DatabaseType.Int64);
        database.Catalog.TryGetView("dbo", "unknown_output", out var unknown).ShouldBeTrue();
        unknown.Columns.ShouldHaveSingleItem().Type.Type.ShouldBe(DatabaseType.String);
        rows.ShouldHaveSingleItem()[0].ShouldBe("x");
    }
    private static async Task<Diagnostic> RecursiveDiagnosticAsync(SqlDatabaseSession session, string sql)
    {
        try
        {
            var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            result.Status.ShouldBe(DatabaseResultStatus.Error);
            return result.Diagnostics.ShouldHaveSingleItem();
        }
        catch (DatabaseException exception) { return exception.Diagnostics.ShouldHaveSingleItem(); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive WITH: non-recursive MERGE sources execute through dependency ordering")]
    public async Task Execute_NonRecursiveMergePrefix_ShouldMaterializeAndUpdate()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-merge", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var result = await session.ExecuteAsync("WITH a AS (SELECT 2 AS n), b AS (SELECT n FROM a) MERGE INTO t USING b ON t.id=b.n WHEN MATCHED THEN UPDATE SET id=9;", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(session, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe([1, 3, 9]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive WITH: recursive domain output retains exact domain identity")]
    public async Task Execute_RecursiveDomain_ShouldFreezeAnchorContract()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-domain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE DOMAIN d AS BIGINT CHECK (VALUE > 0);", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("CREATE VIEW v AS WITH RECURSIVE r(n) AS (SELECT CAST(1 AS d) UNION ALL SELECT n FROM r) SELECT n FROM r;", cancellationToken: CancellationToken.None);
        var error = await RecursiveDiagnosticAsync(session,
            "WITH RECURSIVE r(n) AS (SELECT CAST(1 AS d) UNION ALL SELECT CAST(n AS BIGINT) FROM r) SELECT n FROM r;");

        // Assert
        database.Catalog.TryGetView("dbo", "v", out var view).ShouldBeTrue();
        view.Columns.ShouldHaveSingleItem().DomainName.ShouldBe("d");
        error.Code.ShouldBe("CS02066");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Recursive WITH: implicit default collation does not override a non-default anchor")]
    public async Task Execute_RecursiveImplicitCollation_ShouldFollowCommonCollation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("recursive-collation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE inputs (ci TEXT COLLATE case_insensitive, normal TEXT COLLATE binary);", cancellationToken: CancellationToken.None);

        // Act
        var valid = await RowsAsync(session,
            "WITH RECURSIVE r(n) AS (SELECT ci FROM inputs UNION ALL SELECT normal FROM r CROSS JOIN inputs) SELECT n FROM r;");
        var invalid = await RecursiveDiagnosticAsync(session,
            "WITH RECURSIVE r(n) AS (SELECT normal FROM inputs UNION ALL SELECT ci FROM r CROSS JOIN inputs) SELECT n FROM r;");

        // Assert
        valid.ShouldBeEmpty();
        invalid.Code.ShouldBe("CS02067");
    }
}
