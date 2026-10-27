using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A quoted domain that resembles a built-in type preserves distinct function type identities")]
    public async Task OpenAsync_QuotedDomainResemblingBuiltIn_ShouldPreserveTypeSpelling()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("quoted-type-spelling");
        builder.Options.StorageStrategy = storage;
        await using (var engine = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, "CREATE DOMAIN \"VARCHAR(1)\" AS INT");
            await ExecuteAsync(session, "CREATE FUNCTION builtin_echo(p VARCHAR(1)) RETURNS VARCHAR(1) IMMUTABLE RETURN p");
            await ExecuteAsync(session, "CREATE FUNCTION domain_echo(p \"VARCHAR(1)\") RETURNS \"VARCHAR(1)\" IMMUTABLE RETURN p");
            await ExecuteAsync(session, "CREATE TABLE t(builtin_value VARCHAR(1), domain_value \"VARCHAR(1)\")");
            await ExecuteAsync(session, "INSERT INTO t VALUES('x', 2)");
        }
        var reopened = SqlDatabaseEngine.CreateBuilder("quoted-type-spelling");
        reopened.Options.StorageStrategy = storage.CaptureDurableImages();

        // Act
        await using var second = await reopened.BuildAsync(CancellationToken.None);
        var database = await second.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var read = await database.CreateSessionAsync(CancellationToken.None);
        var rows = await RowsAsync(read, "SELECT builtin_echo(builtin_value), domain_echo(domain_value) FROM t");

        // Assert
        rows.ShouldBe([["x", 2]]);
        database.Catalog.Functions.Single(function => function.Name == "builtin_echo").ParameterTypes.ShouldBe(["VARCHAR(1)"]);
        database.Catalog.Functions.Single(function => function.Name == "domain_echo").ParameterTypes.ShouldBe(["\"VARCHAR(1)\""]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Cold Apply and Verify retain quoted function type identities after domain removal")]
    [InlineData(SqlProvisioningMode.Apply)]
    [InlineData(SqlProvisioningMode.Verify)]
    public async Task BuildAsync_RemovedQuotedDomain_ShouldValidateHistoricalFunctionTargets(SqlProvisioningMode mode)
    {
        // Arrange
        const string sql = """
            CREATE DOMAIN "history.type:(amount)" AS INT;
            CREATE FUNCTION "history.function:(amount)"(p "history.type:(amount)") RETURNS INT IMMUTABLE RETURN p;
            DROP FUNCTION "history.function:(amount)"("history.type:(amount)");
            DROP DOMAIN "history.type:(amount)";
            """;
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("quoted-domain-history");
        builder.Options.StorageStrategy = storage;
        builder.AddDatabase("functions", database => database.Script("001", sql));
        await using (var engine = await builder.BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecuteAsync(session, "CREATE TABLE changed_generation(id INT)");
        }
        var reopened = SqlDatabaseEngine.CreateBuilder("quoted-domain-history");
        reopened.Options.StorageStrategy = storage.CaptureDurableImages();
        reopened.AddDatabase("functions", database =>
        {
            database.Provisioning = mode;
            database.Script("001", sql);
        });

        // Act
        await using var second = await reopened.BuildAsync(CancellationToken.None);
        var restored = await second.OpenDatabaseAsync("functions", CancellationToken.None);

        // Assert
        restored.Catalog.Functions.ShouldBeEmpty();
        restored.Catalog.Domains.ShouldBeEmpty();
        restored.Catalog.Scripts.ShouldHaveSingleItem().CompletedStatements.ShouldBe(4);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Quoted domain separators survive function parameters, columns and cold reopen")]
    [InlineData("positive.type:amount")]
    [InlineData("positive(:type.amount)")]
    public async Task OpenAsync_QuotedDomainSeparators_ShouldPreserveDefinitionIdentity(string name)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("quoted-domain");
        builder.Options.StorageStrategy = storage;
        string quoted = "\"" + name + "\"";
        await using (var engine = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(engine);
            await ExecuteAsync(session, $"CREATE DOMAIN {quoted} AS INT CHECK(VALUE > 0)");
            await ExecuteAsync(session, $"CREATE FUNCTION \"echo.function:amount\"(p {quoted}) RETURNS {quoted} IMMUTABLE RETURN p");
            await ExecuteAsync(session, $"CREATE FUNCTION cast_domain(p INT) RETURNS INT IMMUTABLE RETURN CAST(p AS {quoted})");
            await ExecuteAsync(session, $"CREATE TABLE t(value {quoted})");
            await ExecuteAsync(session, "INSERT INTO t VALUES(3)");
        }
        var reopened = SqlDatabaseEngine.CreateBuilder("quoted-domain");
        reopened.Options.StorageStrategy = storage.CaptureDurableImages();

        // Act
        await using var second = await reopened.BuildAsync(CancellationToken.None);
        var database = await second.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var read = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(read, "SELECT \"echo.function:amount\"(value), cast_domain(value) FROM t")).ShouldBe([[3, 3]]);
        await Should.ThrowAsync<DatabaseException>(() => RowsAsync(read, "SELECT \"echo.function:amount\"(-1) FROM t"));
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, $"DROP DOMAIN {quoted}"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        await ExecuteAsync(read, "DROP TABLE t");
        await ExecuteAsync(read, $"DROP FUNCTION \"echo.function:amount\"({quoted})");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, $"DROP DOMAIN {quoted}"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03302");
        await ExecuteAsync(read, "DROP FUNCTION cast_domain(INT)");
        await ExecuteAsync(read, $"DROP DOMAIN {quoted}");
    }
}
