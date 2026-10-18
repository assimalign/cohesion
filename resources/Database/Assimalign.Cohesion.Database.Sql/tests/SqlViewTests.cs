using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Views: bodies and outer relational contexts compose")]
    [InlineData("SELECT id FROM t WHERE id > 1", "SELECT id FROM v WHERE id = 2", 2)]
    [InlineData("SELECT id AS n FROM t", "SELECT dbo.v.n FROM v ORDER BY n DESC LIMIT 1", 3)]
    [InlineData("SELECT id AS n FROM t ORDER BY ABS(n) DESC LIMIT 1", "SELECT n FROM v", 3)]
    [InlineData("SELECT CAST(COUNT(*) AS INT) AS n FROM t ORDER BY ABS(n)", "SELECT n FROM v", 3)]
    [InlineData("SELECT id FROM t", "SELECT id FROM v ORDER BY id OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY", 2)]
    [InlineData("SELECT id FROM t", "SELECT id FROM v GROUP BY id HAVING COUNT(*) = 1 ORDER BY id LIMIT 1", 1)]
    [InlineData("SELECT id FROM t GROUP BY id HAVING COUNT(*) = 1", "SELECT id FROM v ORDER BY id DESC LIMIT 1", 3)]
    [InlineData("SELECT id FROM t WHERE id IN (SELECT id FROM t WHERE id = 2)", "SELECT id FROM v", 2)]
    [InlineData("SELECT a.id FROM t a JOIN t b ON a.id = b.id", "SELECT id FROM v WHERE id = 2", 2)]
    [InlineData("SELECT id FROM t", "SELECT v.id FROM v JOIN t ON v.id = t.id WHERE t.id = 2", 2)]
    [InlineData("SELECT id FROM t", "SELECT v.id FROM t JOIN v ON v.id = t.id WHERE t.id = 2", 2)]
    [InlineData("SELECT id FROM t", "SELECT id FROM t WHERE id IN (SELECT id FROM v WHERE id = 2)", 2)]
    [InlineData("WITH x AS (SELECT id FROM t) SELECT id FROM x", "SELECT id FROM v WHERE id = 2", 2)]
    [InlineData("WITH x AS (SELECT CAST(COUNT(*) AS INT) AS id) SELECT id FROM x", "SELECT id FROM v", 1)]
    [InlineData("SELECT id FROM t", "WITH t AS (SELECT 99 AS id) SELECT id FROM v WHERE id = 2", 2)]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT u.id FROM t u WHERE u.id = t.id AND u.id = 2)", "SELECT id FROM v", 2)]
    [InlineData("SELECT id FROM t UNION ALL SELECT id FROM t", "SELECT id FROM v ORDER BY id DESC LIMIT 1", 3)]
    [InlineData("SELECT * FROM (SELECT id FROM t) d ORDER BY id DESC LIMIT 1", "SELECT id FROM v", 3)]
    public async Task Execute_ViewContext_ShouldMatchInlineQuery(string body, string query, int expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("views", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS " + body);
        // Act
        var rows = await RowsAsync(session, query);
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([expected]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: stars freeze inside derived queries and set operands")]
    public async Task Execute_NestedStars_ShouldRetainOriginalColumns()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("stars", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT * FROM (SELECT DISTINCT * FROM t) d UNION SELECT * FROM t");
        await ExecuteAsync(session, "CREATE VIEW w AS SELECT * FROM v");
        // Act
        await ExecuteAsync(session, "ALTER TABLE t ADD COLUMN extra INT");
        var rows = await RowsAsync(session, "SELECT * FROM w ORDER BY id");
        await ExecuteAsync(session, "CREATE TABLE copied (id INT)");
        await ExecuteAsync(session, "INSERT INTO copied SELECT * FROM w");
        // Assert
        rows.Count.ShouldBe(3);
        rows[0].ShouldBe([1]);
        (await RowsAsync(session, "SELECT COUNT(*) FROM copied")).ShouldHaveSingleItem().ShouldBe([3L]);
        database.Catalog.Views.Single(view => view.Name == "v").Definition.ShouldNotContain("*", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: schema identity and same-name qualification remain independent")]
    public async Task Execute_SchemaQualifiedViews_ShouldRetainRelationIdentity()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("names", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE SCHEMA a");
        await ExecuteAsync(session, "CREATE SCHEMA b");
        await ExecuteAsync(session, "CREATE VIEW a.v AS SELECT id FROM t WHERE id = 1");
        await ExecuteAsync(session, "CREATE VIEW b.v AS SELECT id FROM t WHERE id = 2");
        // Act
        var rows = await RowsAsync(session, "SELECT a.v.id, b.v.id FROM a.v CROSS JOIN b.v ORDER BY b.v.id");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([1, 2]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: invalid declarations preserve the catalog")]
    [InlineData("CREATE VIEW v AS SELECT id, id FROM t")]
    [InlineData("CREATE VIEW v (a, b) AS SELECT id FROM t")]
    [InlineData("CREATE VIEW v AS SELECT @p FROM t")]
    [InlineData("CREATE VIEW t AS SELECT id FROM t")]
    [InlineData("CREATE VIEW INFORMATION_SCHEMA.TABLES AS SELECT id FROM t")]
    [InlineData("CREATE VIEW absent.v AS SELECT id FROM t")]
    public async Task Execute_InvalidView_ShouldLeaveCatalogUnchanged(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        ulong before = database.Catalog.DdlGeneration;
        // Act
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
        // Assert
        database.Catalog.DdlGeneration.ShouldBe(before);
        database.Catalog.Views.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: table-only targets reject the view kind")]
    [InlineData("INSERT INTO v VALUES (4)")]
    [InlineData("UPDATE v SET id = 4")]
    [InlineData("DELETE FROM v")]
    [InlineData("ALTER TABLE v ADD extra INT")]
    [InlineData("CREATE INDEX ix ON v (id)")]
    [InlineData("DROP TABLE v")]
    [InlineData("DROP TABLE IF EXISTS v")]
    [InlineData("CREATE TABLE IF NOT EXISTS v (id INT)")]
    public async Task Execute_TableOnlyTarget_ShouldNameViewKind(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("readonly", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
        // Assert
        error.Message.ShouldContain("View 'dbo.v'", Case.Sensitive);
        error.Message.ShouldContain("table", Case.Sensitive);
        (await RowsAsync(session, "SELECT COUNT(*) FROM v")).ShouldHaveSingleItem().ShouldBe([3L]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: dependent drops and text-breaking renames are refused")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t DROP COLUMN id")]
    [InlineData("ALTER TABLE t RENAME TO renamed")]
    [InlineData("ALTER TABLE t RENAME COLUMN id TO renamed")]
    public async Task Execute_DependencyChange_ShouldNameDependentView(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("dependencies", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, sql));
        // Assert
        error.Message.ShouldContain("v", Case.Sensitive);
        error.Message.ShouldContain("depend", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: replacement preserves contracts and refuses cycles")]
    public async Task Execute_Replacement_ShouldAppendOnlyAndRetainOldDefinitionOnFailure()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("replace", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        // Act
        foreach (var body in new[] { "SELECT id AS renamed FROM t", "SELECT CAST(id AS BIGINT) AS id FROM t", "SELECT id FROM v" })
        { await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS " + body)); }
        await ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT id, id + 1 AS extra FROM t WHERE id = 2");
        await ExecuteAsync(session, "CREATE VIEW w AS SELECT * FROM v");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE OR REPLACE VIEW v AS SELECT id, extra FROM w"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP VIEW v RESTRICT"));
        var rows = await RowsAsync(session, "SELECT * FROM v");
        await ExecuteAsync(session, "DROP VIEW v CASCADE");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe([2, 3]);
        database.Catalog.Views.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: schema restriction and cascading reach external views")]
    public async Task Execute_DropSchema_ShouldCountViewsAndCascadeExternalDependents()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("schemas", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SCHEMA s");
        await ExecuteAsync(session, "CREATE TABLE s.t (id INT)");
        await ExecuteAsync(session, "CREATE VIEW dbo.external_v AS SELECT id FROM s.t");
        await ExecuteAsync(session, "CREATE VIEW dbo.external_w AS SELECT id FROM external_v");
        await ExecuteAsync(session, "CREATE VIEW s.local_v AS SELECT id FROM s.t");
        // Act
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP SCHEMA s RESTRICT"))).Message.ShouldContain("view");
        await ExecuteAsync(session, "DROP SCHEMA s CASCADE");
        // Assert
        database.Catalog.Views.ShouldBeEmpty();
        database.Catalog.TryGetTable("s", "t", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: functions domains and unused columns have precise dependencies")]
    public async Task Execute_DefinitionAndColumnDependencies_ShouldPreventOrphans()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("definitions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE FUNCTION plus_one(x INT) RETURNS INT IMMUTABLE RETURN x + 1");
        await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK (VALUE > 0)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT plus_one(id) AS n, CAST(id AS positive) AS p FROM t");
        // Act
        var function = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION plus_one(INT)"));
        var domain = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP DOMAIN positive"));
        await ExecuteAsync(session, "ALTER TABLE t ADD extra INT");
        await ExecuteAsync(session, "ALTER TABLE t DROP COLUMN extra");
        // Assert
        function.Message.ShouldContain("view");
        domain.Message.ShouldContain("view");
        (await RowsAsync(session, "SELECT n FROM v WHERE p = 2")).ShouldHaveSingleItem().ShouldBe([3]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: system relations capture through every relation position")]
    [InlineData("SELECT TABLE_NAME FROM v WHERE TABLE_NAME = 't'")]
    [InlineData("SELECT v.TABLE_NAME FROM v JOIN names n ON v.TABLE_NAME = n.name")]
    [InlineData("SELECT v.TABLE_NAME FROM names n JOIN v ON v.TABLE_NAME = n.name")]
    [InlineData("SELECT name FROM names WHERE name IN (SELECT TABLE_NAME FROM v)")]
    public async Task Execute_ViewOverSystemRelation_ShouldCaptureCatalog(string query)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("system", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE names (name TEXT)");
        await ExecuteAsync(session, "INSERT INTO names VALUES ('t')");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES");
        // Act - the second use exercises the cached body plan's capture path.
        await RowsAsync(session, query);
        var rows = await RowsAsync(session, query);
        await ExecuteAsync(session, "DELETE FROM names WHERE name IN (SELECT TABLE_NAME FROM v)");
        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(["t"]);
        (await RowsAsync(session, "SELECT COUNT(*) FROM names")).ShouldHaveSingleItem().ShouldBe([0L]);
        (await RowsAsync(session, "SELECT TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'v'")).ShouldHaveSingleItem().ShouldBe(["VIEW"]);
        (await RowsAsync(session, "SELECT CHECK_OPTION, IS_UPDATABLE FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_NAME = 'v'")).ShouldHaveSingleItem().ShouldBe(["NONE", "NO"]);
        (await RowsAsync(session, "SELECT OBJECT_TYPE, OWNER FROM COHESION_SCHEMA.OBJECT_OWNERSHIP WHERE OBJECT_NAME = 'v'")).ShouldHaveSingleItem().ShouldBe(["VIEW", "Adhoc"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: explicit transactions reject view DDL")]
    [InlineData("CREATE VIEW v AS SELECT id FROM t")]
    [InlineData("DROP VIEW IF EXISTS v")]
    public async Task Execute_ExplicitTransaction_ShouldRejectSelfCommittingViewDdl(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("transactions", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "BEGIN");
        // Act
        var error = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        await ExecuteAsync(session, "ROLLBACK");
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLT003");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: persisted definitions survive file-backed restart")]
    public async Task Open_ViewDefinition_ShouldSurviveRestart()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("files", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await SeedAsync(session);
                await ExecuteAsync(session, "CREATE VIEW v (n) AS SELECT id FROM t WHERE id = 2 UNION SELECT id FROM t WHERE id = 3");
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("files", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            var rows = await RowsAsync(reader, "SELECT n FROM v ORDER BY n");
            // Assert
            rows.Count.ShouldBe(2);
            rows[0].ShouldBe([2]);
            rows[1].ShouldBe([3]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1), (2), (3)");
    }
    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    { await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); }
    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
