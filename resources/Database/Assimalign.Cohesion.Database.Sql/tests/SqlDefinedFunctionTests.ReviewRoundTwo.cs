using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Nested SQL wrappers propagate a weakened native binding even when a branch avoids its call")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAsync_NestedWeakenedNativeVolatility_ShouldRefuseUnresolvedUses(bool domain)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("nested-weakened-native");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        await using (var first = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE FUNCTION wrapped(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)");
            await ExecuteAsync(session, "CREATE FUNCTION nested(p INT) RETURNS BOOLEAN IMMUTABLE RETURN CASE WHEN p = 0 THEN TRUE ELSE wrapped(p) END");
            if (domain)
            {
                await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE = 0 OR nested(VALUE))");
                await ExecuteAsync(session, "CREATE TABLE t(v positive)");
            }
            else
            {
                await ExecuteAsync(session, "CREATE TABLE t(v INT CHECK(v = 0 OR nested(v)))");
            }
            await ExecuteAsync(session, "INSERT INTO t VALUES(1)");
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("nested-weakened-native");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        reopen.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Stable));
        await using var engine = await reopen.BuildAsync(CancellationToken.None);
        var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO t VALUES(0)"));

        // Assert
        failure.Message.ShouldContain("COHSQLE009");
        (await RowsAsync(reader, "SELECT v FROM t")).ShouldBe([[1]]);
        (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT nested(0) FROM t"))).Message.ShouldContain("COHSQLE009");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader,
            "CREATE FUNCTION invalid_nested(p INT) RETURNS BOOLEAN IMMUTABLE RETURN nested(p)"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader,
            "CREATE DOMAIN invalid_domain AS INT CHECK(nested(VALUE))"));
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "CREATE TABLE invalid(v INT CHECK(nested(v)))"));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Weakened native volatility leaves persisted checks readable and recovers after registration is restored")]
    [InlineData("table")]
    [InlineData("domain")]
    [InlineData("wrapper")]
    public async Task OpenAsync_WeakenedNativeVolatility_ShouldDeferFailureUntilUseAndRecover(string kind)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("weakened-native");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        await using (var first = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(first);
            if (kind == "domain")
            {
                await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(allowed(VALUE))");
                await ExecuteAsync(session, "CREATE TABLE t(v positive)");
            }
            else if (kind == "wrapper")
            {
                await ExecuteAsync(session, "CREATE FUNCTION wrapped(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)");
                await ExecuteAsync(session, "CREATE TABLE t(v INT CHECK(wrapped(v)))");
            }
            else
            {
                await ExecuteAsync(session, "CREATE TABLE t(v INT CHECK(allowed(v)))");
            }
            await ExecuteAsync(session, "INSERT INTO t VALUES(1)");
        }
        var weakenedStorage = storage.CaptureDurableImages();
        var weakened = SqlDatabaseEngine.CreateBuilder("weakened-native");
        weakened.Options.StorageStrategy = weakenedStorage;
        weakened.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Volatile));

        // Act
        await using (var engine = await weakened.BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);

            // Assert
            (await RowsAsync(session, "SELECT v FROM t")).ShouldBe([[1]]);
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES(2)"))).Message.ShouldContain("COHSQLE009");
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE t SET v = 2"))).Message.ShouldContain("COHSQLE009");
            (await RowsAsync(session, "SELECT v FROM t")).ShouldBe([[1]]);
            await ExecuteAsync(session, "CREATE TABLE cleanup(id INT)");
            await ExecuteAsync(session, "DROP TABLE cleanup");
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE TABLE invalid(v INT CHECK(allowed(v)))"));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "CREATE DOMAIN invalid_domain AS INT CHECK(allowed(VALUE))"));
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
                "CREATE FUNCTION invalid_wrapper(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)"))).Message.ShouldContain("COHSQLD001");
        }

        var restored = SqlDatabaseEngine.CreateBuilder("weakened-native");
        restored.Options.StorageStrategy = weakenedStorage.CaptureDurableImages();
        restored.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var restoredEngine = await restored.BuildAsync(CancellationToken.None);
        var restoredDatabase = await restoredEngine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var restoredSession = await restoredDatabase.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(restoredSession, "INSERT INTO t VALUES(2)");
        await ExecuteAsync(restoredSession, "UPDATE t SET v = 3 WHERE v = 1");
        (await RowsAsync(restoredSession, "SELECT v FROM t ORDER BY v")).ShouldBe([[2], [3]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Domain parameters preserve their casts for direct and explicitly cast arguments before and after reopen")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_DomainParameterIdentity_ShouldPreserveItsCast(bool reopen)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var cents = SqlType.CreateDomain("cents", SqlType.BigInt);
        var cast = SqlCast.Create<long, string>(cents, SqlType.Text,
            static value => "cents:" + value.ToString(CultureInfo.InvariantCulture), SqlCastContext.Explicit);
        var builder = SqlDatabaseEngine.CreateBuilder("domain-parameter");
        builder.Options.StorageStrategy = storage;
        builder.Types.Add(cents).AddCast(cast);
        await using (var first = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE TABLE t(v cents)");
            await ExecuteAsync(session, "INSERT INTO t VALUES(12)");
            await ExecuteAsync(session, "CREATE FUNCTION stringify(p cents) RETURNS TEXT IMMUTABLE RETURN CAST(p AS TEXT)");
            await ExecuteAsync(session, "CREATE FUNCTION stringify_collated(p cents, label TEXT) RETURNS TEXT IMMUTABLE RETURN CAST(p AS TEXT)");

            if (!reopen)
            {
                // Act
                var rows = await RowsAsync(session,
                    "SELECT CAST(v AS TEXT), stringify(v), stringify(CAST(12 AS cents)), stringify_collated(v, 'A' COLLATE case_insensitive) FROM t");

                // Assert
                rows.ShouldBe([["cents:12", "cents:12", "cents:12", "cents:12"]]);
                return;
            }
        }
        var next = SqlDatabaseEngine.CreateBuilder("domain-parameter");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        next.Types.Add(cents).AddCast(cast);
        await using var engine = await next.BuildAsync(CancellationToken.None);
        var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var reopenedRows = await RowsAsync(reader,
            "SELECT CAST(v AS TEXT), stringify(v), stringify(CAST(12 AS cents)), stringify_collated(v, 'A' COLLATE case_insensitive) FROM t");

        // Assert
        reopenedRows.ShouldBe([["cents:12", "cents:12", "cents:12", "cents:12"]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL return domains widen INTEGER values for a BIGINT check like columns and arguments")]
    public async Task ExecuteAsync_ReturnDomainWidening_ShouldConvertCheckArgument()
    {
        // Arrange
        var positive = SqlType.CreateDomain("positive", SqlType.Integer,
            SqlScalarFunction.Create("positive_check", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        var builder = SqlDatabaseEngine.CreateBuilder("return-domain");
        builder.Types.Add(positive);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t(v positive)");
        await ExecuteAsync(session, "INSERT INTO t VALUES(12)");
        await ExecuteAsync(session, "CREATE FUNCTION domain_argument(p positive) RETURNS INT IMMUTABLE RETURN p");
        await ExecuteAsync(session, "CREATE FUNCTION domain_result(p INT) RETURNS positive IMMUTABLE RETURN p");
        (await RowsAsync(session, "SELECT v, CAST(v AS positive), domain_argument(v) FROM t")).ShouldBe([[12, 12, 12]]);

        // Act
        var rows = await RowsAsync(session, "SELECT domain_result(v) FROM t");

        // Assert
        rows.ShouldBe([[12]]);
        await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT domain_result(CAST(-v AS INT)) FROM t"));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SQL return-domain NULL checks honor strict and called-on-NULL input behavior")]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ReturnDomainNull_ShouldHonorCheckNullBehavior(bool calledOnNullInput, bool rejectNull)
    {
        // Arrange
        int checks = 0;
        var positive = SqlType.CreateDomain("positive", SqlType.Integer,
            SqlScalarFunction.Create("positive_check", (long? value) => { checks++; return value is null ? !rejectNull : value > 0; },
                SqlFunctionVolatility.Immutable, calledOnNullInput ? SqlNullBehavior.CalledOnNullInput : SqlNullBehavior.ReturnsNullOnNullInput));
        var builder = SqlDatabaseEngine.CreateBuilder("return-domain-null");
        builder.Types.Add(positive);
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t(v INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES(NULL)");
        await ExecuteAsync(session, "CREATE FUNCTION domain_result(p INT) RETURNS positive IMMUTABLE RETURN p");

        // Act
        if (calledOnNullInput && rejectNull)
        {
            var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT domain_result(v) FROM t"));

            // Assert
            failure.ConstraintName.ShouldBe("positive");
        }
        else
        {
            var rows = await RowsAsync(session, "SELECT domain_result(v) FROM t");

            // Assert
            rows.ShouldBe([[null]]);
        }

        // Assert
        checks.ShouldBe(calledOnNullInput ? 1 : 0);
    }
}
