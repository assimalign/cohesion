using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlDefinedFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL bodies use column, explicit and default invocation collations after reopen")]
    public async Task Open_InvocationCollations_ShouldReachSqlBodies()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("invocation-collation");
        builder.Options.StorageStrategy = storage;
        await using (var first = await builder.BuildAsync())
        {
            await using var session = await SessionAsync(first);
            await ExecuteAsync(session, "CREATE FUNCTION is_a(p TEXT) RETURNS BOOLEAN IMMUTABLE RETURN p = 'a'");
            await ExecuteAsync(session, "CREATE FUNCTION like_a(p TEXT) RETURNS BOOLEAN IMMUTABLE RETURN p LIKE 'a%'");
            await ExecuteAsync(session, "CREATE FUNCTION classify(p TEXT) RETURNS TEXT IMMUTABLE RETURN CASE WHEN is_a(p) THEN 'a' ELSE p END");
            await ExecuteAsync(session, "CREATE TABLE t(s TEXT COLLATE case_insensitive)");
            await ExecuteAsync(session, "INSERT INTO t VALUES ('A'), ('a'), ('B')");
        }
        var reopen = SqlDatabaseEngine.CreateBuilder("invocation-collation");
        reopen.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var engine = await reopen.BuildAsync();
        var database = await engine.OpenDatabaseAsync("functions");
        await using var reader = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var column = await RowsAsync(reader, "SELECT s = 'a', is_a(s), like_a(s) FROM t");
        var explicitCollation = await RowsAsync(reader, "SELECT is_a('A' COLLATE case_insensitive), like_a('A' COLLATE case_insensitive), is_a(s COLLATE binary) FROM t");
        var defaults = await RowsAsync(reader, "SELECT is_a('A'), like_a('A') FROM t");

        // Assert
        column.ShouldBe([[true, true, true], [true, true, true], [false, false, false]]);
        explicitCollation.ShouldBe([[true, true, false], [true, true, true], [true, true, false]]);
        defaults.ShouldBe([[false, false], [false, false], [false, false]]);
        (await RowsAsync(reader, "SELECT classify(s), COUNT(*) FROM t GROUP BY classify(s) ORDER BY classify(s)")).ShouldBe([["a", 2L], ["B", 1L]]);
        (await RowsAsync(reader, "SELECT classify(s) FROM t ORDER BY classify(s)")).ShouldBe([["a"], ["a"], ["B"]]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL function calls without argument collation use the database default")]
    public async Task ExecuteAsync_NoInvocationCollation_ShouldUseDatabaseDefault()
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        var database = await engine.CreateDatabaseAsync("collated", Collation.CaseInsensitive, CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE FUNCTION is_a(p TEXT) RETURNS BOOLEAN IMMUTABLE RETURN p = 'a'");
        await ExecuteAsync(session, "CREATE TABLE t(id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");

        // Act
        var rows = await RowsAsync(session, "SELECT is_a('A'), is_a('A' COLLATE binary) FROM t");

        // Assert
        rows.ShouldBe([[true, false]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - STRICT SQL functions validate domain arguments before NULL dispatch")]
    [InlineData("CAST(-1 AS INT), 1")]
    [InlineData("CAST(-1 AS INT), NULL")]
    [InlineData("NULL, 1")]
    public async Task ExecuteAsync_StrictDomainArgument_ShouldEnforceDomainBeforeNullDispatch(string arguments)
    {
        // Arrange
        await using var engine = await BuildAsync(_ => { });
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t(id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        await ExecuteAsync(session, "CREATE DOMAIN positive AS INT NOT NULL CHECK(VALUE > 0)");
        await ExecuteAsync(session, "CREATE FUNCTION echo_positive(p positive, q INT) RETURNS INT STRICT RETURN p");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, $"SELECT echo_positive({arguments}) FROM t"));

        // Assert
        failure.Message.ShouldContain("positive");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - STRICT still skips SQL body execution after domain coercion")]
    public async Task ExecuteAsync_ValidStrictDomainArguments_ShouldSkipBodyForNull()
    {
        // Arrange
        int calls = 0;
        await using var engine = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("body_call", (int value) => { calls++; return value; })));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t(id INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (1)");
        await ExecuteAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE > 0)");
        await ExecuteAsync(session, "CREATE FUNCTION echo_positive(p positive, q INT) RETURNS INT STRICT RETURN body_call(p)");
        await ExecuteAsync(session, "CREATE FUNCTION echo_one(p positive) RETURNS INT STRICT RETURN body_call(p)");

        // Act
        var rows = await RowsAsync(session, "SELECT echo_positive(CAST(1 AS INT), NULL), echo_positive(NULL, 1), echo_one(NULL) FROM t");

        // Assert
        rows.ShouldBe([[null, null, null]]);
        calls.ShouldBe(0);
        await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT echo_one(CAST(-1 AS INT)) FROM t"));
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - SQL bodies compile once for each invocation collation and reuse across statements")]
    public async Task ExecuteAsync_RepeatedInvocationCollation_ShouldReuseBoundBody()
    {
        // Arrange
        int folds = 0;
        await using var engine = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("fold_probe",
            (int _) => { folds++; return true; }, SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE FUNCTION is_a(p TEXT) RETURNS BOOLEAN IMMUTABLE RETURN fold_probe(1) AND p = 'a'");
        await ExecuteAsync(session, "CREATE TABLE t(s TEXT COLLATE case_insensitive)");
        await ExecuteAsync(session, "INSERT INTO t VALUES ('A'), ('a'), ('B')");
        int initialFolds = folds;

        // Act
        var first = await RowsAsync(session, "SELECT is_a(s), is_a(s COLLATE binary) FROM t");
        var second = await RowsAsync(session, "SELECT is_a(s), is_a(s COLLATE binary) FROM t");

        // Assert
        first.ShouldBe([[true, false], [true, true], [false, false]]);
        second.ShouldBe(first);
        folds.ShouldBe(initialFolds + 1);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - SQL domain invocation checks bind their collation before reading rows")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_EmptyCollatedInput_ShouldPrepareDomainCheckBeforeRows(bool resultDomain)
    {
        // Arrange
        int folds = 0;
        await using var engine = await BuildAsync(functions => functions.Add(SqlScalarFunction.Create("fold_probe",
            (int _) => { folds++; return true; }, SqlFunctionVolatility.Immutable)));
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE DOMAIN text_a AS TEXT CHECK(fold_probe(1) AND VALUE = 'a')");
        await ExecuteAsync(session, resultDomain
            ? "CREATE FUNCTION echo_a(p TEXT) RETURNS text_a STRICT RETURN p"
            : "CREATE FUNCTION echo_a(p text_a) RETURNS TEXT STRICT RETURN p");
        await ExecuteAsync(session, "CREATE TABLE t(s TEXT COLLATE case_insensitive)");
        int initialFolds = folds;

        // Act: an empty table still binds the call and its checks before row evaluation.
        var empty = await RowsAsync(session, "SELECT echo_a(s) FROM t");

        // Assert
        empty.ShouldBeEmpty();
        folds.ShouldBe(initialFolds + 1);
        await ExecuteAsync(session, "INSERT INTO t VALUES ('A'), ('a')");
        (await RowsAsync(session, "SELECT echo_a(s) FROM t")).ShouldBe([["A"], ["a"]]);
        folds.ShouldBe(initialFolds + 1);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Native STRICT domain arguments use the same pre-dispatch coercion")]
    public async Task ExecuteAsync_NativeStrictDomainArgument_ShouldValidateBeforeDispatch()
    {
        // Arrange
        int calls = 0;
        int checks = 0;
        var positive = SqlType.CreateDomain("native_positive", SqlType.Integer,
            SqlScalarFunction.Create("native_positive_check", (int value) => { checks++; return value > 0; }, SqlFunctionVolatility.Immutable));
        var builder = SqlDatabaseEngine.CreateBuilder("native-strict-domain");
        builder.Types.Add(positive);
        builder.Functions.Add(new NativeDomainEchoFunction(positive, () => calls++));
        await using var engine = await builder.BuildAsync();
        await using var session = await SessionAsync(engine);
        await ExecuteAsync(session, "CREATE TABLE t(v INT, q INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (-1, NULL)");

        // Act
        var failure = await Should.ThrowAsync<SqlConstraintViolationException>(() => RowsAsync(session, "SELECT native_domain_echo(v, q) FROM t"));

        // Assert
        failure.Message.ShouldContain("native_positive");
        calls.ShouldBe(0);
        checks.ShouldBe(1);
        await ExecuteAsync(session, "UPDATE t SET v = 1");
        (await RowsAsync(session, "SELECT native_domain_echo(v, q) FROM t")).ShouldBe([[null]]);
        calls.ShouldBe(0);
        checks.ShouldBe(2);
        await ExecuteAsync(session, "UPDATE t SET q = 1");
        (await RowsAsync(session, "SELECT native_domain_echo(v, q) FROM t")).ShouldBe([[1]]);
        calls.ShouldBe(1);
        checks.ShouldBe(3);
    }
}
