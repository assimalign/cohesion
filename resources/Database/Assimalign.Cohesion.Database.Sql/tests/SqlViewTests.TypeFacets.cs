using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlViewTests
{
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
        error.Diagnostics[0].Code.ShouldBe("COHSQLE033");
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
        error.Diagnostics[0].Code.ShouldBe("COHSQLE033");
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
