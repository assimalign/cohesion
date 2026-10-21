using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1ScalarRegressionTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - T1 special forms: definitions and temporal CHECKs enforce after reopen")]
    public async Task Execute_SpecialFormsInDefinitions_ShouldEnforceAfterReopen()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-t1-regression", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("special-definitions", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                foreach (string ddl in new[]
                {
                    "CREATE FUNCTION passthrough(x INT) RETURNS INT IMMUTABLE RETURN x",
                    "CREATE FUNCTION nonzero(x INT) RETURNS INT IMMUTABLE RETURN NULLIF(passthrough(x), 0)",
                    "CREATE FUNCTION year_of(d DATE) RETURNS NUMERIC IMMUTABLE RETURN EXTRACT(YEAR FROM d)",
                    "CREATE FUNCTION one_day() RETURNS INTERVAL IMMUTABLE RETURN INTERVAL '1' DAY",
                    "CREATE FUNCTION origin() RETURNS DATE IMMUTABLE RETURN DATE '2024-01-01'",
                    "CREATE DOMAIN nonzero_int AS INT CHECK (NULLIF(VALUE, 0) IS NOT NULL)",
                    "CREATE DOMAIN current_year AS DATE CHECK (EXTRACT(YEAR FROM VALUE) = 2024)",
                    "CREATE TABLE t (n nonzero_int, d current_year, ts TIMESTAMP, CONSTRAINT ck_n CHECK (NULLIF(n, 0) IS NOT NULL), CONSTRAINT ck_year CHECK (EXTRACT(YEAR FROM d) = 2024), CONSTRAINT ck_duration CHECK (ts + one_day() > ts), CONSTRAINT ck_calendar CHECK (origin() <= ts))",
                }) { await session.ExecuteAsync(ddl, cancellationToken: CancellationToken.None); }
                await session.ExecuteAsync("INSERT INTO t VALUES (1, DATE '2024-03-04', TIMESTAMP '2024-03-04 12:00:00')", cancellationToken: CancellationToken.None);
                var dependency = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("DROP FUNCTION passthrough(INT)", cancellationToken: CancellationToken.None).AsTask());
                dependency.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHSQLD002");
            }

            // Act
            await using var reopened = SqlDatabaseEngine.Create("special-reopened", new() { RootPath = root });
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var second = await opened.CreateSessionAsync(CancellationToken.None);
            await second.ExecuteAsync("INSERT INTO t VALUES (2, DATE '2024-06-01', TIMESTAMP '2024-06-01 12:00:00')", cancellationToken: CancellationToken.None);

            // Assert
            (await Rows(second, "SELECT nonzero(n), year_of(d) FROM t ORDER BY n")).ShouldBe([[1, 2024m], [2, 2024m]]);
            var domain = await SqlConstraintAssertions.ThrowAsync(() => second.ExecuteAsync("INSERT INTO t VALUES (0, DATE '2024-01-01', TIMESTAMP '2024-01-01 00:00:00')", cancellationToken: CancellationToken.None).AsTask());
            domain.ConstraintKind.ShouldBe("DOMAIN");
            var check = await SqlConstraintAssertions.ThrowAsync(() => second.ExecuteAsync("INSERT INTO t VALUES (3, DATE '2024-01-01', TIMESTAMP '2023-01-01 00:00:00')", cancellationToken: CancellationToken.None).AsTask());
            check.ConstraintKind.ShouldBe("CHECK");
            check.Message.ShouldContain("ck_calendar", Case.Sensitive);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - T1 special forms: nested calls retain dependency and volatility validation")]
    [InlineData("NULLIF(CURRENT_DATE, DATE '2000-01-01')")]
    [InlineData("EXTRACT(YEAR FROM CURRENT_DATE)")]
    [InlineData("COALESCE(NULL, CURRENT_DATE)")]
    public async Task Create_ImmutableDefinitionWithNestedStableCall_ShouldRefuse(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("nested-volatility", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"CREATE FUNCTION invalid() RETURNS TEXT IMMUTABLE RETURN {expression}", cancellationToken: CancellationToken.None).AsTask());
        failure.Message.ShouldContain("CURRENT_DATE", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - T1 correlation: each special form prepares its child for each outer row")]
    public async Task Select_CorrelatedChildrenInSpecialForms_ShouldUseEachOuterRow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("special-correlation", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE p (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE c (pid INT, v INT, at TIMESTAMP)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO p VALUES (1), (2), (3)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO c VALUES (1, 5, TIMESTAMP '2024-01-01 00:00:00'), (2, 0, TIMESTAMP '2025-01-01 00:00:00')", cancellationToken: CancellationToken.None);

        // Act
        var rows = await Rows(session, "SELECT p.id, NULLIF((SELECT MAX(c.v) FROM c WHERE c.pid = p.id), 0), EXTRACT(YEAR FROM (SELECT MAX(c.at) FROM c WHERE c.pid = p.id)), COALESCE((SELECT MAX(c.v) FROM c WHERE c.pid = p.id), -1), CASE WHEN (SELECT MAX(c.v) FROM c WHERE c.pid = p.id) = 0 THEN 10 ELSE (SELECT MAX(c.v) FROM c WHERE c.pid = p.id) END FROM p ORDER BY p.id");

        // Assert
        rows.ShouldBe([[1, 5, 2024m, 5, 5], [2, null, 2025m, 0, 10], [3, null, null, -1L, null]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NULLIF: CHAR equality normalizes both argument orders and preserves the first value")]
    public async Task Select_NullIfCharacterComparands_ShouldFollowEquality()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("nullif-character", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (c CHAR(3))", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES ('a')", cancellationToken: CancellationToken.None);

        // Act / Assert
        await using var result = (await session.ExecuteAsync("SELECT c = 'a ', NULLIF(c, 'a ') IS NULL, NULLIF('a ', c) IS NULL, NULLIF(c, @p) IS NULL, NULLIF(@p, c) IS NULL, NULLIF(@q, c), NULLIF(c, 'b') FROM t", new Dictionary<string, object?> { ["p"] = "a ", ["q"] = "b " }, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[true, true, true, true, true, "b ", "a  "]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DEFAULT: stable and volatile functions execute for CREATE and ALTER")]
    [InlineData("CURRENT_TIMESTAMP", "TIMESTAMPTZ")]
    [InlineData("NOW()", "TIMESTAMPTZ")]
    [InlineData("CURRENT_DATE", "DATE")]
    [InlineData("CURRENT_TIME", "TIME")]
    [InlineData("LOCALTIME", "TIME")]
    [InlineData("LOCALTIMESTAMP", "TIMESTAMP")]
    [InlineData("GEN_RANDOM_UUID()", "UUID")]
    [InlineData("NULLIF(NOW(), NOW())", "TIMESTAMPTZ")]
    [InlineData("wrapped_now()", "TIMESTAMPTZ")]
    public async Task Create_NonImmutableDefault_ShouldExecuteOnAssignment(string expression, string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-volatility", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION wrapped_now() RETURNS TIMESTAMPTZ STABLE RETURN NOW()", cancellationToken: CancellationToken.None);

        // Act / Assert
        await session.ExecuteAsync($"CREATE TABLE defaults (v {type} DEFAULT {expression})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO defaults DEFAULT VALUES", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT v FROM defaults")).Count.ShouldBe(1);
        await session.ExecuteAsync($"CREATE TABLE existing (v {type})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"ALTER TABLE existing ALTER COLUMN v SET DEFAULT {expression}", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"ALTER TABLE existing ADD COLUMN extra {type} DEFAULT {expression}", cancellationToken: CancellationToken.None);
        database.Catalog.TryGetTable("dbo", "existing", out var table).ShouldBeTrue();
        table.Columns.Count.ShouldBe(2);
        await session.ExecuteAsync("INSERT INTO existing DEFAULT VALUES", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT v FROM existing")).Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Temporal arithmetic: unsupported operators and pairs fail on empty input")]
    [InlineData("d * 2", "*")]
    [InlineData("d / 2", "/")]
    [InlineData("d % 2", "%")]
    [InlineData("ts * iv", "*")]
    [InlineData("ts / iv", "/")]
    [InlineData("iv % iv", "%")]
    [InlineData("iv * 2", "*")]
    [InlineData("COALESCE(i, d) * 2", "*")]
    [InlineData("CASE WHEN i = 1 THEN i ELSE d END / 2", "/")]
    [InlineData("d + ts", "+")]
    public async Task Select_InvalidTemporalArithmetic_ShouldRefuseDuringPlanning(string expression, string op)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal-operators", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (i INT, d DATE, ts TIMESTAMP, iv INTERVAL)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT i FROM t WHERE {expression} IS NULL", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("COHSQLE003");
        failure.Message.ShouldContain(op, Case.Sensitive);
        var check = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"CREATE TABLE refused (i INT, d DATE, ts TIMESTAMP, iv INTERVAL, CHECK ({expression} IS NULL))", cancellationToken: CancellationToken.None).AsTask());
        check.Diagnostics[0].Code.ShouldBe("COHSQLE003");
        (await Rows(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'refused'")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Temporal arithmetic: UTC range and integer NULL metadata remain correct")]
    public async Task Select_InstantRangeAndNullArithmetic_ShouldPreserveSemantics()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal-range", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await Rows(session, "SELECT TIMESTAMPTZ '9999-12-31 22:00:00+02:00' + INTERVAL '3' HOUR, TIMESTAMPTZ '0001-01-01 01:00:00-02:00' - INTERVAL '2' HOUR FROM t")).ShouldBe([[new DateTimeOffset(9999, 12, 31, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(1, 1, 1, 1, 0, 0, TimeSpan.Zero)]]);
        foreach (string op in new[] { "+", "-", "*", "/", "%" })
        {
            await using var result = (await session.ExecuteAsync($"SELECT id {op} NULL, id {op} @p FROM t WHERE id = 0", new Dictionary<string, object?> { ["p"] = null }, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns[0].Type.ShouldBe(DatabaseType.Int64);
            result.Columns[1].Type.ShouldBe(DatabaseType.Int64);
            (await SqlT1ScalarTests.ReadAsync(result)).ShouldBeEmpty();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - T1 identifiers: unquoted function-named columns bind across DML")]
    [InlineData("position")]
    [InlineData("sign")]
    [InlineData("reverse")]
    [InlineData("ln")]
    [InlineData("exp")]
    [InlineData("now")]
    [InlineData("interval")]
    public async Task Execute_UnquotedFunctionNamedColumn_ShouldBindAsColumn(string name)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("identifier-regression", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync($"CREATE TABLE {name} ({name} INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO {name} ({name}) VALUES (1)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"UPDATE {name} SET {name} = 2", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE INDEX ix ON {name} ({name})", cancellationToken: CancellationToken.None);

        // Assert
        (await Rows(session, $"SELECT {name}.{name} AS {name} FROM {name} AS {name}")).ShouldBe([[2]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - T1 clocks: niladic forms execute in VALUES and reject temporal LIMIT counts")]
    [InlineData("CURRENT_DATE", "DATE")]
    [InlineData("CURRENT_TIME", "TIME")]
    [InlineData("CURRENT_TIMESTAMP", "TIMESTAMPTZ")]
    [InlineData("LOCALTIME", "TIME")]
    [InlineData("LOCALTIMESTAMP", "TIMESTAMP")]
    public async Task Execute_NiladicClockWithoutScope_ShouldInsertValueAndRejectLimit(string expression, string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("niladic-positions", new() { TimeProvider = new ScalarClock(SqlT1ScalarTests.Instant) });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (v {type})", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync($"INSERT INTO t VALUES ({expression})", cancellationToken: CancellationToken.None);

        // Assert
        object expected = type switch { "DATE" => new DateOnly(2024, 3, 4), "TIME" => new TimeOnly(12, 34, 56), "TIMESTAMP" => SqlT1ScalarTests.Instant.UtcDateTime, _ => SqlT1ScalarTests.Instant };
        (await Rows(session, "SELECT v FROM t")).ShouldBe([[expected]]);
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT v FROM t LIMIT {expression}", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("COHSQLE018");
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
