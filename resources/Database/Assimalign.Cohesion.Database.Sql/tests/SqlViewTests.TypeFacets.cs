using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Views: USING facet coercions remain readable before and after reopen")]
    [InlineData("VARCHAR(10)", "VARCHAR(20)", "'value'", "value", DatabaseType.String)]
    [InlineData("NUMERIC(5,2)", "NUMERIC(7,2)", "12.34", "12.34", DatabaseType.Decimal)]
    [InlineData("NUMERIC(5,2)", "NUMERIC(6,3)", "12.34", "12.34", DatabaseType.Decimal)]
    [InlineData("CHAR(10)", "VARCHAR(20)", "'value'", "value     ", DatabaseType.Character)]
    [InlineData("VARCHAR(10)", "CHAR(20)", "'value'", "value", DatabaseType.String)]
    public async Task Open_UsingDifferentFacets_ShouldRetainUnconstrainedContract(string left, string right, string literal, string expected, DatabaseType type)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("view-using-facets", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE a (k " + left + ")");
            await ExecuteAsync(session, "CREATE TABLE b (k " + right + ")");
            await ExecuteAsync(session, "INSERT INTO a VALUES (" + literal + ")");
            await ExecuteAsync(session, "INSERT INTO b VALUES (" + literal + ")");
            await ExecuteAsync(session, "CREATE VIEW v AS SELECT k FROM a JOIN b USING (k)");

            // Act / Assert: creation and the first rebind must agree before persistence too.
            var column = database.Catalog.Views.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();
            column.Type.Type.ShouldBe(type);
            column.Type.MaxLength.ShouldBeNull();
            column.Type.Precision.ShouldBeNull();
            column.Type.Scale.ShouldBeNull();
            var row = (await RowsAsync(session, "SELECT k FROM v")).ShouldHaveSingleItem();
            System.Convert.ToString(row[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("view-using-facets", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var rows = await RowsAsync(reader, "SELECT k FROM v");
        var metadata = await RowsAsync(reader, "SELECT CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");

        // Assert
        System.Convert.ToString(rows.ShouldHaveSingleItem()[0], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
        metadata.ShouldHaveSingleItem().ShouldBe([null, null, null]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: branch facets require matching domains and identical domains survive")]
    [InlineData("CASE WHEN id = 1 THEN CAST(id AS amount) ELSE CAST(id AS {0}) END")]
    [InlineData("COALESCE(CAST(id AS amount), CAST(id AS {0}))")]
    [InlineData("CAST(id AS amount) AS y FROM t UNION SELECT CAST(id AS {0})")]
    [InlineData("CASE WHEN id = 1 THEN (SELECT CAST(u.id AS amount) FROM t u WHERE u.id = t.id) ELSE (SELECT CAST(u.id AS {0}) FROM t u WHERE u.id = t.id) END")]
    [InlineData("COALESCE(domain_value(id), CAST(id AS {0}))")]
    public async Task Open_BranchDomains_ShouldRetainOnlyAgreedIdentityAndFacets(string projection)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("view-branch-domains", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE DOMAIN amount AS NUMERIC(5,2)");
            await ExecuteAsync(session, "CREATE DOMAIN other_amount AS NUMERIC(5,2)");
            await ExecuteAsync(session, "CREATE FUNCTION domain_value(x INT) RETURNS amount IMMUTABLE RETURN CAST(x AS amount)");
            foreach (string target in new[] { "amount", "other_amount", "NUMERIC(5,2)" })
            {
                string view = target == "amount" ? "same_domain" : target == "other_amount" ? "other_domain" : "base_type";
                string body = string.Format(System.Globalization.CultureInfo.InvariantCulture, projection, target);
                await ExecuteAsync(session, "CREATE VIEW " + view + " AS SELECT " + body + " AS y FROM t");
                var column = System.Linq.Enumerable.Single(database.Catalog.Views, item => item.Name == view).Columns.ShouldHaveSingleItem();
                column.DomainName.ShouldBe(target == "amount" ? "amount" : null);
                (await RowsAsync(session, "SELECT NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '" + view + "'"))
                    .ShouldHaveSingleItem().ShouldBe(target == "amount" ? [5L, 2L] : [null, null]);
                (await RowsAsync(session, "SELECT * FROM " + view)).Count.ShouldBe(3);
            }
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("view-branch-domains", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var metadata = await RowsAsync(reader, "SELECT TABLE_NAME, NUMERIC_PRECISION, NUMERIC_SCALE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN ('same_domain', 'other_domain', 'base_type') ORDER BY TABLE_NAME");

        // Assert
        metadata.Count.ShouldBe(3);
        metadata[0].ShouldBe(["base_type", null, null]);
        metadata[1].ShouldBe(["other_domain", null, null]);
        metadata[2].ShouldBe(["same_domain", 5L, 2L]);
        foreach (string view in new[] { "same_domain", "other_domain", "base_type" })
        {
            (await RowsAsync(reader, "SELECT * FROM " + view)).Count.ShouldBe(3);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: capturing full set operand metadata retains identical domain identities")]
    [InlineData("CAST(id AS positive)")]
    [InlineData("domain_value(id)")]
    public async Task Execute_DomainSetOperand_ShouldKeepDomainWithTypeFacets(string projection)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-domain-set", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE DOMAIN positive AS DECIMAL(5,2) CHECK (VALUE > 0)");
        await ExecuteAsync(session, "CREATE FUNCTION domain_value(x INT) RETURNS positive IMMUTABLE RETURN CAST(x AS positive)");

        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + projection + " AS n FROM t UNION SELECT " + projection + " FROM t");

        // Assert
        var column = database.Catalog.Views.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();
        column.DomainName.ShouldBe("positive");
        column.Type.Precision.ShouldBe(5);
        column.Type.Scale.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: catalog refusal diagnostics reach the client and preserve the connection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CatalogViewRefusal_ShouldReachWireDiagnostics(bool oversized)
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = SqlClient.Create(new()
        {
            Settings = new() { Database = ServerTestHarness.DatabaseName, EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync("CREATE VIEW v AS SELECT CAST(id AS DECIMAL(5,2)) AS n FROM users", cancellationToken: TestTimeout.Token());
        string sql = oversized ? "CREATE VIEW large_v AS SELECT '" + new string('x', 9000) + "' AS n FROM users"
            : "CREATE OR REPLACE VIEW v AS SELECT CAST(id AS DECIMAL(6,3)) AS n FROM users";

        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(sql, cancellationToken: TestTimeout.Token()));
        var retained = await connection.QueryAsync("SELECT n FROM v ORDER BY n", cancellationToken: TestTimeout.Token());

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CS03033");
        retained.Count.ShouldBe(2);
        connection.IsOpen.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: direct and nested columns always report nullable through source constraint changes")]
    public async Task Execute_ChangedSourceNullability_ShouldKeepViewColumnsNullable()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-nullability", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t (id INT NOT NULL)");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id FROM t");
        await ExecuteAsync(session, "CREATE VIEW w AS SELECT id FROM v");
        var before = await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN ('v', 'w') ORDER BY TABLE_NAME");

        // Act
        await ExecuteAsync(session, "ALTER TABLE t ALTER COLUMN id DROP NOT NULL");
        await ExecuteAsync(session, "INSERT INTO t VALUES (NULL)");
        var after = await RowsAsync(session, "SELECT IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME IN ('v', 'w') ORDER BY TABLE_NAME");
        var rows = await RowsAsync(session, "SELECT id FROM w");

        // Assert
        before.Count.ShouldBe(2);
        before[0].ShouldBe(["YES"]);
        before[1].ShouldBe(["YES"]);
        after.Count.ShouldBe(2);
        after[0].ShouldBe(["YES"]);
        after[1].ShouldBe(["YES"]);
        rows.ShouldHaveSingleItem().ShouldBe([null]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: identical alternative and function modifiers survive sets and reopen")]
    [InlineData("SELECT f(id) AS y FROM t UNION ALL SELECT f(id) FROM t", true)]
    [InlineData("SELECT COALESCE(CAST(id AS VARCHAR(5)), CAST(id AS VARCHAR(5))) AS y FROM t", false)]
    [InlineData("SELECT CASE WHEN id = 1 THEN CAST(id AS VARCHAR(5)) ELSE CAST(id AS VARCHAR(5)) END AS y FROM t", false)]
    [InlineData("SELECT NULLIF(CAST(id AS VARCHAR(5)), CAST('x' AS VARCHAR(9))) AS y FROM t", false)]
    [InlineData("SELECT f(id) AS y FROM t UNION ALL SELECT CASE WHEN id = 1 THEN f(id) ELSE f(id) END FROM t", true)]
    [InlineData("SELECT COALESCE(CAST(id AS VARCHAR(5)), CAST(id AS VARCHAR(5))) AS y FROM t UNION SELECT CAST(id AS VARCHAR(5)) FROM t", false)]
    [InlineData("SELECT CASE WHEN id = 1 THEN CAST(id AS DECIMAL(5,2)) ELSE CAST(id AS DECIMAL(5,2)) END AS y FROM t GROUP BY id", true)]
    [InlineData("WITH x AS (SELECT COALESCE(CAST(1 AS VARCHAR(5)), CAST(2 AS VARCHAR(5))) AS y) SELECT y FROM x", false)]
    public async Task Open_IdenticalTypeFacets_ShouldRetainMetadataAndRefuseReplacement(string body, bool numeric)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("view-common-facets", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE FUNCTION f(x INT) RETURNS DECIMAL(5,2) IMMUTABLE RETURN CAST(x AS DECIMAL(5,2))");
            await ExecuteAsync(session, "CREATE VIEW v AS " + body);
        }

        // Act
        await using var reopened = SqlDatabaseEngine.Create("view-common-facets", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        var metadata = await RowsAsync(reader, "SELECT NUMERIC_PRECISION, NUMERIC_SCALE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");
        await RowsAsync(reader, "SELECT * FROM v");
        string changed = body.Replace("f(id)", "CAST(id AS DECIMAL(6,3))").Replace("DECIMAL(5,2)", "DECIMAL(6,3)").Replace("VARCHAR(5)", "VARCHAR(9)");
        var error = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "CREATE OR REPLACE VIEW v AS " + changed));

        // Assert
        metadata.ShouldHaveSingleItem().ShouldBe(numeric ? [5L, 2L, null] : [null, null, 5L]);
        error.Diagnostics[0].Code.ShouldBe("CS03033");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: different or absent alternative modifiers produce unconstrained output")]
    [InlineData("CASE WHEN id = 1 THEN CAST(id AS DECIMAL(5,2)) ELSE CAST(id AS DECIMAL(6,3)) END")]
    [InlineData("CASE WHEN id = 1 THEN CAST(id AS DECIMAL(5,2)) END")]
    [InlineData("COALESCE(CAST(id AS DECIMAL(5,2)), NULL)")]
    [InlineData("COALESCE(CAST(id AS DECIMAL(5,2)), CAST(id AS DECIMAL(6,3)))")]
    [InlineData("COALESCE(CAST(id AS VARCHAR(5)), CAST(id AS VARCHAR(9)))")]
    [InlineData("CAST(id AS DECIMAL(5,2)) AS y FROM t UNION SELECT CAST(id AS DECIMAL(6,3))")]
    [InlineData("f(id) AS y FROM t UNION SELECT CAST(id AS DECIMAL(6,3))")]
    public async Task Execute_DifferentTypeFacets_ShouldReportUnconstrainedOutput(string projection)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("view-different-facets", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE FUNCTION f(x INT) RETURNS DECIMAL(5,2) IMMUTABLE RETURN CAST(x AS DECIMAL(5,2))");

        // Act
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT " + projection + " AS y FROM t");
        var metadata = await RowsAsync(session, "SELECT NUMERIC_PRECISION, NUMERIC_SCALE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'v'");

        // Assert
        metadata.ShouldHaveSingleItem().ShouldBe([null, null, null]);
    }
}
