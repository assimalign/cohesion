using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Persisted calls distinguish native pseudo and variadic signatures after reopen")]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task OpenAsync_NativeSignatureKinds_ShouldPreserveExactIdentity(bool variadic, bool retainOriginal)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("native-signatures");
        builder.Options.StorageStrategy = storage;
        SqlType parameter = variadic ? SqlType.Integer : SqlType.Any;
        SqlType? tail = variadic ? SqlType.Integer : null;
        builder.Functions.Add(new NativeSignatureFunction(parameter, tail, false));
        await using (var first = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE FUNCTION body(p INT) RETURNS BOOLEAN IMMUTABLE RETURN native_gate(p)");
            await ExecuteAsync(session, "CREATE TABLE checked(v INT CHECK(native_gate(v)))");
            await ExecuteAsync(session, "CREATE TABLE defaults(id INT, v BOOLEAN DEFAULT native_gate(1))");
            await ExecuteAsync(session, "INSERT INTO defaults(id) VALUES(0)");
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("native-signatures");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        if (retainOriginal)
        {
            reopen.Functions.Add(new NativeSignatureFunction(parameter, tail, false));
        }
        reopen.Functions.Add(new NativeSignatureFunction(variadic ? SqlType.Integer : SqlType.AnyElement, null, true));

        // Act
        await using var engine = await reopen.BuildAsync();
        var database = await engine.OpenDatabaseAsync("functions");
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        if (retainOriginal)
        {
            await ExecuteAsync(reader, "INSERT INTO defaults(id) VALUES(1)");
            (await RowsAsync(reader, "SELECT v, body(id) FROM defaults")).ShouldBe([[false, false], [false, false]]);
            await Should.ThrowAsync<SqlConstraintViolationException>(() => ExecuteAsync(reader, "INSERT INTO checked VALUES(1)"));
        }
        else
        {
            (await RowsAsync(reader, "SELECT v FROM defaults")).ShouldBe([[false]]);
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO defaults(id) VALUES(1)"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO checked VALUES(1)"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
            (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT body(id) FROM defaults"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A native variadic signature does not own a requested fixed DROP signature")]
    public async Task ExecuteAsync_NativeVariadicOnly_ShouldRefuseDropAsMissing()
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("native-variadic-drop");
        builder.Functions.Add(new NativeSignatureFunction(SqlType.BigInt, SqlType.BigInt, false));
        await using var engine = await builder.BuildAsync();
        await using var session = await SessionAsync(engine);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION native_gate(BIGINT)"));

        // Assert
        failure.Message.ShouldContain("COHSQLD001");
        failure.Message.ShouldNotContain("COHSQLD003");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A SQL function removed through the catalog cannot be replaced by another overload on reopen")]
    public async Task OpenAsync_RecordedSqlFunctionRemoved_ShouldRefuseEveryStoredCall()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("removed-sql-signature");
        builder.Options.StorageStrategy = storage;
        await using (var first = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE FUNCTION allowed(p BIGINT) RETURNS BOOLEAN IMMUTABLE RETURN p > 0");
            await ExecuteAsync(session, "CREATE FUNCTION body(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)");
            await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(allowed(VALUE))");
            await ExecuteAsync(session, "CREATE TABLE checked(v INT CHECK(allowed(v)))");
            await ExecuteAsync(session, "CREATE TABLE defaults(id INT, v BOOLEAN DEFAULT allowed(0))");
            await ExecuteAsync(session, "CREATE TABLE domained(v positive)");
            await ExecuteAsync(session, "INSERT INTO defaults(id) VALUES(0)");
            await ExecuteAsync(session, "CREATE FUNCTION allowed(p INT) RETURNS BOOLEAN IMMUTABLE RETURN TRUE");
            var database = await first.OpenDatabaseAsync("functions");
            string original = database.Catalog.Functions.Single(function => function.Name == "allowed" && function.ParameterTypes[0] == "BIGINT").Signature;
            await database.Catalog.DropFunctionAsync(original, CancellationToken.None);
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("removed-sql-signature");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();

        // Act
        await using var engine = await reopen.BuildAsync();
        var reopened = await engine.OpenDatabaseAsync("functions");
        await using var reader = await reopened.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await RowsAsync(reader, "SELECT v FROM defaults")).ShouldBe([[false]]);
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO checked VALUES(-1)"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO defaults(id) VALUES(1)"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(reader, "INSERT INTO domained VALUES(-1)"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
        (await Should.ThrowAsync<DatabaseException>(() => RowsAsync(reader, "SELECT body(id) FROM defaults"))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
    }
}
