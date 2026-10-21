using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted CHECK retains its overload after a volatile exact overload is added")]
    public async Task ExecuteAsync_LaterOverload_ShouldPreserveCheckIdentity()
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE FUNCTION allowed(p BIGINT) RETURNS BOOLEAN IMMUTABLE RETURN p > 0");
        await ExecuteAsync(session, "CREATE TABLE t(v INT CHECK(allowed(v)))");
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES(-1)"));
        // Act
        await ExecuteAsync(session, "CREATE FUNCTION allowed(p INT) RETURNS BOOLEAN VOLATILE RETURN TRUE");
        // Assert
        await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "INSERT INTO t VALUES(-1)"));
        (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session, "DROP FUNCTION allowed(BIGINT)"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLD002");
        await ExecuteAsync(session, "DROP FUNCTION allowed(INT)");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Persisted defaults, domain checks and SQL bodies retain overload identities after reopen")]
    public async Task OpenAsync_LaterOverloads_ShouldPreserveEveryStoredCall()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-identities", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = await BuildAsync(_ => { }, root))
            {
                await using var session = await SessionAsync(engine);
                await ExecuteAsync(session, "CREATE FUNCTION value_of(p BIGINT) RETURNS INT IMMUTABLE RETURN 1");
                await ExecuteAsync(session, "CREATE FUNCTION allowed(p BIGINT) RETURNS BOOLEAN IMMUTABLE RETURN p > 0");
                await ExecuteAsync(session, "CREATE FUNCTION body(p INT) RETURNS INT IMMUTABLE RETURN value_of(p)");
                await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(allowed(VALUE))");
                await ExecuteAsync(session, "CREATE TABLE t(id INT, v INT DEFAULT value_of(0), p positive)");
                await ExecuteAsync(session, "CREATE FUNCTION value_of(p INT) RETURNS INT IMMUTABLE RETURN 2");
                await ExecuteAsync(session, "CREATE FUNCTION allowed(p INT) RETURNS BOOLEAN IMMUTABLE RETURN TRUE");
            }
            // Act
            await using var reopened = await BuildAsync(_ => { }, root);
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var read = await database.CreateSessionAsync();
            await ExecuteAsync(read, "INSERT INTO t(id,p) VALUES(1,1)");
            // Assert
            (await RowsAsync(read, "SELECT v, body(id) FROM t")).ShouldBe([[1, 1]]);
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "INSERT INTO t(id,p) VALUES(2,-1)"));
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "DROP FUNCTION value_of(BIGINT)"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLD002");
            (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "DROP FUNCTION allowed(BIGINT)"))).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLD002");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
    /// <param name="originalNative">Whether the original overload is registered by the engine.</param>
    /// <param name="laterNative">Whether the later overload is registered by the reopening engine.</param>
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Native, SQL and mixed overload additions preserve every persisted call across reopen")]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OpenAsync_MixedLaterOverloads_ShouldPreserveRecordedIdentities(bool originalNative, bool laterNative)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-identities", Guid.NewGuid().ToString("N"));
        void RegisterOriginal(SqlFunctionCollection functions)
        {
            if (!originalNative) { return; }
            functions.Add(SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable));
            functions.Add(SqlScalarFunction.Create("value_of", static (long _) => 1, SqlFunctionVolatility.Immutable));
        }
        try
        {
            await using (var engine = await BuildAsync(RegisterOriginal, root))
            {
                await using var session = await SessionAsync(engine);
                if (!originalNative)
                {
                    await ExecuteAsync(session, "CREATE FUNCTION allowed(p BIGINT) RETURNS BOOLEAN IMMUTABLE RETURN p > 0");
                    await ExecuteAsync(session, "CREATE FUNCTION value_of(p BIGINT) RETURNS INT IMMUTABLE RETURN 1");
                }
                await ExecuteAsync(session, "CREATE FUNCTION body(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)");
                await ExecuteAsync(session, "CREATE DOMAIN positive AS INT DEFAULT 4 CHECK(allowed(VALUE))");
                await ExecuteAsync(session, "CREATE TABLE checked(v INT CHECK(allowed(v)))");
                await ExecuteAsync(session, "CREATE TABLE defaults(id INT, v INT DEFAULT value_of(0), p positive)");
                if (!laterNative)
                {
                    await ExecuteAsync(session, "CREATE FUNCTION allowed(p INT) RETURNS BOOLEAN VOLATILE RETURN TRUE");
                    await ExecuteAsync(session, "CREATE FUNCTION value_of(p INT) RETURNS INT RETURN 2");
                }
            }
            // Act
            await using var reopened = await BuildAsync(functions =>
            {
                RegisterOriginal(functions);
                if (!laterNative) { return; }
                functions.Add(SqlScalarFunction.Create("allowed", static (int _) => true));
                functions.Add(SqlScalarFunction.Create("value_of", static (int _) => 2));
            }, root);
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var read = await database.CreateSessionAsync();
            await ExecuteAsync(read, "INSERT INTO defaults(id) VALUES(1)");
            // Assert
            (await RowsAsync(read, "SELECT v, p, body(CAST(-1 AS INT)) FROM defaults")).ShouldBe([[1, 4, false]]);
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "INSERT INTO checked VALUES(-1)"));
            await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "INSERT INTO defaults(id,p) VALUES(2,-1)"));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A missing recorded native overload never redirects to an available overload")]
    public async Task OpenAsync_MissingRecordedOverload_ShouldRefuseEveryUseWithCohsqle009()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-identities", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = await BuildAsync(functions => functions.Add(
                SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable)), root))
            {
                await using var session = await SessionAsync(engine);
                await ExecuteAsync(session, "CREATE FUNCTION body(p INT) RETURNS BOOLEAN IMMUTABLE RETURN allowed(p)");
                await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(allowed(VALUE))");
                await ExecuteAsync(session, "CREATE TABLE checked(v INT CHECK(allowed(v)))");
                await ExecuteAsync(session, "CREATE TABLE defaults(id INT, v BOOLEAN DEFAULT allowed(1))");
                await ExecuteAsync(session, "CREATE TABLE domains(v positive)");
                await ExecuteAsync(session, "INSERT INTO checked VALUES(1)");
            }
            // Act
            await using var reopened = await BuildAsync(functions => functions.Add(
                SqlScalarFunction.Create("allowed", static (int _) => true, SqlFunctionVolatility.Immutable)), root);
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var read = await database.CreateSessionAsync();
            // Assert
            (await RowsAsync(read, "SELECT v FROM checked")).ShouldBe([[1]]);
            foreach (string sql in new[] { "INSERT INTO checked VALUES(1)", "INSERT INTO defaults(id) VALUES(1)",
                "INSERT INTO domains VALUES(1)", "SELECT body(v) FROM checked" })
            {
                (await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, sql))).Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - A persisted CHECK rechecks Immutable admission when the registered signature changes volatility")]
    public async Task OpenAsync_ChangedRegisteredVolatility_ShouldRefuseCheckUse()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-e4-identities", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = await BuildAsync(functions => functions.Add(
                SqlScalarFunction.Create("allowed", static (long value) => value > 0, SqlFunctionVolatility.Immutable)), root))
            {
                await using var session = await SessionAsync(engine);
                await ExecuteAsync(session, "CREATE TABLE t(v INT CHECK(allowed(v)))");
            }
            // Act
            await using var reopened = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("allowed", static (long _) => true)), root);
            var database = await reopened.OpenDatabaseAsync("functions");
            await using var read = await database.CreateSessionAsync();
            // Assert
            var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(read, "INSERT INTO t VALUES(-1)"));
            failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "COHSQLE009");
            failure.Message.ShouldContain("IMMUTABLE");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }
}
