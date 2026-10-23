using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SQL function domain dependencies propagate weakened CHECK bindings and recover with the native registration")]
    [InlineData("cast", "positive")]
    [InlineData("parameter", "positive")]
    [InlineData("result", "positive")]
    [InlineData("cast", "\"positive.type:amount\"")]
    [InlineData("parameter", "\"positive.type:amount\"")]
    [InlineData("result", "\"positive.type:amount\"")]
    public async Task OpenAsync_UnresolvedDomainDependency_ShouldRefuseFunctionUseAndRecover(string dependency, string domain)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("domain-binding-recovery");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        await using (var first = await builder.BuildAsync(CancellationToken.None))
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, $"CREATE DOMAIN {domain} AS INT CHECK(allowed(VALUE))");
            await ExecuteAsync(session, dependency switch
            {
                "parameter" => $"CREATE FUNCTION use_domain(p {domain}) RETURNS BOOLEAN IMMUTABLE RETURN TRUE",
                "result" => $"CREATE FUNCTION use_domain(p INT) RETURNS {domain} IMMUTABLE RETURN p",
                _ => $"CREATE FUNCTION use_domain(p INT) RETURNS BOOLEAN IMMUTABLE RETURN CASE WHEN p = 0 THEN TRUE ELSE CAST(p AS {domain}) > 0 END",
            });
            await ExecuteAsync(session, dependency == "result"
                ? "CREATE TABLE t(v INT CHECK(v = 0 OR use_domain(v) > 0))"
                : "CREATE TABLE t(v INT CHECK(v = 0 OR use_domain(v)))");
            await ExecuteAsync(session, "INSERT INTO t VALUES(1)");
        }
        var weakenedStorage = storage.CaptureDurableImages();
        var weakened = SqlDatabaseEngine.CreateBuilder("domain-binding-recovery");
        weakened.Options.StorageStrategy = weakenedStorage;
        weakened.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Volatile));

        // Act
        await using (var engine = await weakened.BuildAsync(CancellationToken.None))
        {
            var database = await engine.OpenDatabaseAsync("functions", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES(0)"));

            // Assert
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01009");
            (await RowsAsync(session, "SELECT v FROM t")).ShouldBe([[1]]);
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "UPDATE t SET v = 0"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01009");
            (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(session, "SELECT use_domain(0) FROM t"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01009");
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
                $"CREATE FUNCTION invalid_parameter(p {domain}) RETURNS INT IMMUTABLE RETURN p"));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
                $"CREATE FUNCTION invalid_result(p INT) RETURNS {domain} IMMUTABLE RETURN p"));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,
                $"CREATE FUNCTION invalid_cast(p INT) RETURNS INT IMMUTABLE RETURN CAST(p AS {domain})"));
        }

        var restored = SqlDatabaseEngine.CreateBuilder("domain-binding-recovery");
        restored.Options.StorageStrategy = weakenedStorage.CaptureDurableImages();
        restored.Functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var restoredEngine = await restored.BuildAsync(CancellationToken.None);
        var restoredDatabase = await restoredEngine.OpenDatabaseAsync("functions", CancellationToken.None);
        await using var restoredSession = await restoredDatabase.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(restoredSession, "INSERT INTO t VALUES(2)");
        (await RowsAsync(restoredSession, "SELECT v FROM t ORDER BY v")).ShouldBe([[1], [2]]);
    }
}
