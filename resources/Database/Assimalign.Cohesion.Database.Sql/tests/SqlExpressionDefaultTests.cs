using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlExpressionDefaultTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - DEFAULT: immutable expressions evaluate once per declaration")]
    public async Task Declare_ImmutableDefault_ShouldRetainValidatedBinding()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("immutable-default");
        builder.Functions.Add(SqlScalarFunction.Create("tracked", (int value) => { calls++; return value; }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync("CREATE TABLE t (id INT, n INT DEFAULT tracked(1) + 2)", cancellationToken: CancellationToken.None);
        calls.ShouldBe(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)", cancellationToken: CancellationToken.None);
        calls.ShouldBe(1);
        await session.ExecuteAsync("ALTER TABLE t ADD extra INT DEFAULT tracked(2) + 2", cancellationToken: CancellationToken.None);
        calls.ShouldBe(2);
        await session.ExecuteAsync("ALTER TABLE t ALTER n SET DEFAULT tracked(3) + 2", cancellationToken: CancellationToken.None);
        calls.ShouldBe(3);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (3)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT n, extra FROM t ORDER BY id")).Select(row => (row[0], row[1]))
            .ShouldBe(new (object?, object?)[] { (3, 4), (3, 4), (5, 4) });
        calls.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DEFAULT: row evaluation shares the transaction clock and generates distinct UUIDs")]
    public async Task Insert_ExpressionDefaults_ShouldEvaluatePerRowWithTransactionClock()
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("expression-defaults", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, n INT DEFAULT 1 + 2, label TEXT DEFAULT UPPER('x'), c INT DEFAULT CAST('1' AS INT), stamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP, wall TIMESTAMP DEFAULT CURRENT_TIMESTAMP, day DATE DEFAULT CURRENT_DATE, tm TIME DEFAULT LOCALTIME, uid UUID DEFAULT GEN_RANDOM_UUID());", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        var begin = clock.Instant;
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1), (2)", cancellationToken: CancellationToken.None);
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) SELECT id + 2 FROM t", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("UPDATE t SET stamp = DEFAULT WHERE id = 1", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);

        // Assert
        var rows = await Rows(session, "SELECT n, label, c, stamp, wall, day, tm, uid FROM t ORDER BY id");
        rows.Count.ShouldBe(4);
        rows.ShouldAllBe(row => Equals(row[0], 3) && Equals(row[1], "X") && Equals(row[2], 1) && Equals(row[3], begin)
            && Equals(row[4], DateTime.SpecifyKind(begin.UtcDateTime, DateTimeKind.Unspecified))
            && Equals(row[5], DateOnly.FromDateTime(begin.UtcDateTime)) && Equals(row[6], TimeOnly.FromDateTime(begin.UtcDateTime)));
        rows.Select(row => row[7]).Distinct().Count().ShouldBe(4);
        await session.ExecuteAsync("INSERT INTO t (id, stamp) VALUES (5, DEFAULT)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT stamp FROM t WHERE id = 5")).Single()[0].ShouldBe(clock.Instant);
        clock.Instant += TimeSpan.FromHours(1);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (6)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT stamp FROM t WHERE id = 6")).Single()[0].ShouldBe(clock.Instant);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ADD COLUMN: stable missing values freeze across SET DEFAULT and restart")]
    public async Task AddColumn_StableDefault_ShouldFreezeMissingValueAcrossReopen()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-expression-defaults", Guid.NewGuid().ToString("N"));
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        var frozen = clock.Instant;
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("fast-default", new() { RootPath = root, TimeProvider = clock }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("ALTER TABLE t ADD stamp TIMESTAMPTZ DEFAULT CURRENT_TIMESTAMP NOT NULL", cancellationToken: CancellationToken.None);
                clock.Instant += TimeSpan.FromDays(1);
                await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)", cancellationToken: CancellationToken.None);
                (await Rows(session, "SELECT stamp FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { frozen, clock.Instant });
                await session.ExecuteAsync("ALTER TABLE t ALTER stamp SET DEFAULT NOW()", cancellationToken: CancellationToken.None);
            }
            await using var reopened = SqlDatabaseEngine.Create("fast-default-reopen", new() { RootPath = root, TimeProvider = clock });
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var second = await opened.CreateSessionAsync(CancellationToken.None);

            // Act / Assert
            clock.Instant += TimeSpan.FromDays(1);
            await second.ExecuteAsync("INSERT INTO t (id) VALUES (3)", cancellationToken: CancellationToken.None);
            var rows = await Rows(second, "SELECT stamp FROM t ORDER BY id");
            rows[0][0].ShouldBe(frozen);
            rows[2][0].ShouldBe(clock.Instant);
            (await Rows(second, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' AND COLUMN_NAME = 'stamp'")).Single()[0].ShouldBe("NOW()");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ADD COLUMN: volatile defaults require an empty table")]
    public async Task AddColumn_VolatileDefault_ShouldRejectLiveRowsBeforeMutation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("volatile-add", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("ALTER TABLE t ADD uid UUID DEFAULT GEN_RANDOM_UUID()", cancellationToken: CancellationToken.None).AsTask());
        error.Diagnostics.Single().Code.ShouldBe("COHSQLE027");
        error.Message.ShouldContain("uid", Case.Sensitive);
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(1);
        await session.ExecuteAsync("DELETE FROM t", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ADD uid UUID DEFAULT GEN_RANDOM_UUID()", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2), (3)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT uid FROM t")).Select(row => row[0]).Distinct().Count().ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: invalid definitions retain their diagnostic through every DDL path")]
    [InlineData("id + 1", "COHSQLE013")]
    [InlineData("@p", "COHSQLE013")]
    [InlineData("SUM(1)", "COHSQLE013")]
    [InlineData("-'x'", "COHSQLE003")]
    [InlineData("(1 / 0)", "COHSQLE001")]
    [InlineData("CAST('x' AS INT)", "COHSQLE022")]
    [InlineData("UPPER('x')", "COHSQLE013")]
    public async Task Declare_InvalidDefault_ShouldRejectEveryDdlPathBeforeMutation(string expression, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, bad INT DEFAULT 7)", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string sql in new[] { $"CREATE TABLE rejected (id INT, bad INT DEFAULT {expression})",
            $"ALTER TABLE t ADD COLUMN rejected INT DEFAULT {expression}", $"ALTER TABLE t ALTER bad SET DEFAULT {expression}" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe(code);
            failure.Message.ShouldContain(sql.Contains("ADD COLUMN", StringComparison.Ordinal) ? "rejected" : "bad", Case.Sensitive);
            if (code == "COHSQLE001")
            {
                failure.Message.ShouldContain("Division by zero", Case.Sensitive);
                failure.Message.ShouldNotContain("cannot be stored", Case.Sensitive);
            }
        }
        database.Catalog.TryGetTable("dbo", "rejected", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(2);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT bad FROM t")).Single()[0].ShouldBe(7);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER COLUMN: expression defaults survive all nullability alteration forms")]
    public async Task AlterColumn_ExpressionDefault_ShouldPreserveCanonicalExpression()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-expression", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (n INT DEFAULT 1 + 2)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t DEFAULT VALUES", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string sql in new[] { "ALTER TABLE t ALTER n SET NOT NULL", "ALTER TABLE t ALTER n DROP NOT NULL",
            "ALTER TABLE t ALTER COLUMN n INT NOT NULL DEFAULT (1 + 2)" })
        {
            await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
            await session.ExecuteAsync("INSERT INTO t DEFAULT VALUES", cancellationToken: CancellationToken.None);
        }
        (await Rows(session, "SELECT n FROM t")).Select(row => row[0]).ShouldBe(new object?[] { 3, 3, 3, 3 });
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns[0].IsNullable.ShouldBeFalse();
        table.Columns[0].DefaultLiteral.ShouldBe("1 + 2");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT NULL: declaration checks type while assignments enforce NOT NULL")]
    [InlineData("INT", "NULLIF(1, 1)", "1")]
    [InlineData("INT", "CAST(NULL AS INT)", "1")]
    [InlineData("TIMESTAMPTZ", "NULLIF(NOW(), NOW())", "CURRENT_TIMESTAMP")]
    public async Task Declare_NullExpressionDefault_ShouldCheckNullabilityOnlyForAssignedRows(string type, string expression, string value)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-default", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act: declaration and nullability alterations do not assign the default.
        await session.ExecuteAsync($"CREATE TABLE t (n {type} NOT NULL DEFAULT {expression})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO t VALUES ({value})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ALTER n DROP NOT NULL", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("ALTER TABLE t ALTER n SET NOT NULL", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE empty (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"ALTER TABLE empty ADD n {type} NOT NULL DEFAULT {expression}", cancellationToken: CancellationToken.None);

        // Assert: actual assignments fail atomically, including ADD COLUMN over live rows.
        foreach (string sql in new[] { "INSERT INTO t DEFAULT VALUES", "UPDATE t SET n = DEFAULT",
            "INSERT INTO empty (id) VALUES (1)", $"ALTER TABLE t ADD rejected {type} NOT NULL DEFAULT {expression}" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe("COHSQLE024");
        }
        (await Rows(session, "SELECT n FROM t")).Count.ShouldBe(1);
        (await Rows(session, "SELECT id FROM empty")).ShouldBeEmpty();
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: loading never evaluates a changed immutable function")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_ChangedImmutableDefault_ShouldReadAndFailOnlyWhenAssigned(bool overlong)
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-default-load", Guid.NewGuid().ToString("N"));
        bool changed = false;
        int calls = 0;
        SqlDatabaseEngineBuilder Builder()
        {
            var builder = SqlDatabaseEngine.CreateBuilder("default-load");
            builder.Options.RootPath = root;
            builder.Functions.Add(SqlScalarFunction.Create("tenant_code", (int _) =>
            {
                calls++;
                return !changed ? "ACME" : overlong ? "TOO LONG FOR COLUMN" : throw new InvalidOperationException("changed immutable default");
            }, SqlFunctionVolatility.Immutable));
            return builder;
        }
        try
        {
            await using (var engine = await Builder().BuildAsync(CancellationToken.None))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("CREATE TABLE t (id INT, code VARCHAR(8) DEFAULT tenant_code(1))", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)", cancellationToken: CancellationToken.None);
                calls.ShouldBe(1);
                changed = true;
                calls = 0;
                // A definition-environment refresh also loads the table without running its DEFAULT.
                await session.ExecuteAsync("CREATE FUNCTION refresh_default(x INT) RETURNS INT IMMUTABLE RETURN x", cancellationToken: CancellationToken.None);
                (await Rows(session, "SELECT code FROM t")).Single()[0].ShouldBe("ACME");
                calls.ShouldBe(0);
            }
            await using var reopened = await Builder().BuildAsync(CancellationToken.None);
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var second = await opened.CreateSessionAsync(CancellationToken.None);

            // Act / Assert
            calls.ShouldBe(0);
            (await Rows(second, "SELECT id, code FROM t")).Single().ShouldBe(new object?[] { 1, "ACME" });
            await second.ExecuteAsync("INSERT INTO t VALUES (2, 'EXPLICIT')", cancellationToken: CancellationToken.None);
            calls.ShouldBe(0);
            var failure = await Should.ThrowAsync<DatabaseException>(() => second.ExecuteAsync("INSERT INTO t (id) VALUES (3)", cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe(overlong ? "COHSQLE012" : "COHSQLE007");
            calls.ShouldBe(1);
            (await Rows(second, "SELECT id FROM t ORDER BY id")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2 });
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: nonfinite constants and frozen stable values share the literal diagnostic")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Declare_NonfiniteDefault_ShouldRejectBeforePublication(string text)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("nonfinite-default");
        builder.Functions.Add(SqlScalarFunction.Create("stable_nonfinite", (string _) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), SqlFunctionVolatility.Stable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, n DOUBLE PRECISION DEFAULT 7)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string expression in new[] { $"'{text}'", $"CAST('{text}' AS DOUBLE PRECISION)" })
        {
            foreach (string sql in new[] { $"CREATE TABLE rejected (n DOUBLE PRECISION DEFAULT {expression})",
                $"ALTER TABLE t ADD rejected DOUBLE PRECISION DEFAULT {expression}", $"ALTER TABLE t ALTER n SET DEFAULT {expression}" })
            {
                var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
                failure.Diagnostics.Single().Code.ShouldBe("COHSQLE002");
                failure.Message.ShouldContain("Floating-point defaults must be finite.", Case.Sensitive);
                failure.Message.ShouldNotContain("must be a literal", Case.Sensitive);
            }
        }
        foreach (string table in new[] { "t", "empty" })
        {
            if (table == "empty") { await session.ExecuteAsync("CREATE TABLE empty (id INT)", cancellationToken: CancellationToken.None); }
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"ALTER TABLE {table} ADD rejected DOUBLE PRECISION DEFAULT stable_nonfinite('x')", cancellationToken: CancellationToken.None).AsTask());
            failure.Diagnostics.Single().Code.ShouldBe("COHSQLE002");
        }
        database.Catalog.TryGetTable("dbo", "rejected", out _).ShouldBeFalse();
        database.Catalog.TryGetTable("dbo", "t", out var original).ShouldBeTrue();
        original.Columns.Count.ShouldBe(2);
        (await Rows(session, "SELECT n FROM t")).Single()[0].ShouldBe(7d);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: volatile nonfinite values follow ordinary per-row assignment")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public async Task Insert_VolatileNonfiniteDefault_ShouldMatchExplicitCast(string text)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("volatile-nonfinite");
        builder.Functions.Add(SqlScalarFunction.Create("volatile_nonfinite", (string _) => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, n DOUBLE PRECISION DEFAULT volatile_nonfinite('x'))", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync($"INSERT INTO t VALUES (1, CAST('{text}' AS DOUBLE PRECISION))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (2)", cancellationToken: CancellationToken.None);

        // Assert
        var rows = await Rows(session, "SELECT n FROM t ORDER BY id");
        ((double)rows[0][0]!).Equals((double)rows[1][0]!).ShouldBeTrue();
        double.IsFinite((double)rows[1][0]!).ShouldBeFalse();
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
