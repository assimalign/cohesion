using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1ScalarPositionsTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - T1 scalar names: compiled schemas remain idempotent after names become functions")]
    public async Task ApplySchema_NewBuiltinColumnNames_ShouldRemainANoOpOnReapply()
    {
        // Arrange
        string[] names = ["char_length", "character_length", "concat_ws", "position", "strpos", "left", "right", "lpad", "rpad", "reverse", "ceil", "sign", "exp", "ln", "log10", "date_trunc", "gen_random_uuid"];
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
            [new CompiledSchemaTable("scalar_names", "Tests.ScalarNames",
                names.Select(name => new CompiledSchemaColumn(name, DatabaseType.String, true)).ToArray(), null, [], [])], []);
        await using var engine = SqlDatabaseEngine.Create("scalar-schema-identifiers", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);

        // Act
        (await database.ApplySchemaAsync(schema)).WasAlreadyApplied.ShouldBeFalse();
        var reapplied = await database.ApplySchemaAsync(SqlCompiledSchemaSerializer.Deserialize(schema.CanonicalDocument));

        // Assert
        reapplied.WasAlreadyApplied.ShouldBeTrue();
        reapplied.OperationCount.ShouldBe(0);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO scalar_names VALUES (" + string.Join(", ", names.Select(_ => "'value'")) + ");", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT " + string.Join(", ", names.Select(name => "\"" + name + "\"")) + " FROM scalar_names;"))
            .ShouldBe([names.Select(_ => (object?)"value").ToArray()]);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - T1 scalar names: applications cannot register new built-in names at another arity")]
    public void Add_NewBuiltinName_ShouldRemainReservedAtEveryArity()
    {
        // Arrange
        var functions = SqlDatabaseEngine.CreateBuilder("t1-reserved").Functions;
        var original = new HashSet<string>(["UPPER", "LOWER", "LENGTH", "ABS"], StringComparer.OrdinalIgnoreCase);
        var names = SqlT1ScalarTests.Cases().Select(item => (string)item[0]!).Where(name => !original.Contains(name));

        // Act / Assert
        foreach (string name in names)
        {
            Should.Throw<ArgumentException>(() => functions.Add(SqlScalarFunction.Create(name,
                static (long a, long b, long c, long d) => a + b + c + d)));
        }
    }

    public static IEnumerable<object?[]> Positions() =>
    [
        ["REVERSE('abc')", "TEXT", "cba", "= 'cba'"],
        ["FLOOR(2.5)", "DECIMAL", 2m, "= 2"],
        ["NULLIF('x', 'x')", "TEXT", null, "IS NULL"],
        ["CURRENT_DATE", "DATE", new DateOnly(2024, 3, 4), "= DATE '2024-03-04'"],
        ["GEN_RANDOM_UUID()", "UUID", typeof(Guid), "IS NOT NULL"],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 functions: every family returns values across expression positions")]
    [MemberData(nameof(Positions))]
    public async Task Execute_EveryExpressionPosition_ShouldReturnEachFamilyValue(string expression, string type, object? expected, string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("scalar-positions", new() { TimeProvider = new ScalarClock(SqlT1ScalarTests.Instant) });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE dest (v {type});", cancellationToken: CancellationToken.None);

        // Act / Assert
        var projected = await Rows(session, $"SELECT {expression} FROM t;");
        projected.Count.ShouldBe(2);
        foreach (var row in projected) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
        (await Rows(session, $"SELECT id FROM t WHERE {expression} {predicate} ORDER BY id;")).ShouldBe([[1], [2]]);
        (await Rows(session, $"SELECT a.id, b.id FROM t a JOIN t b ON {expression} {predicate} ORDER BY a.id, b.id;")).ShouldBe([[1, 1], [1, 2], [2, 1], [2, 2]]);
        var grouped = await Rows(session, $"SELECT {expression}, COUNT(*) FROM t GROUP BY {expression} HAVING {expression} {predicate} ORDER BY {expression};");
        grouped.Sum(row => (long)row[1]!).ShouldBe(2);
        foreach (var row in grouped) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
        var ordered = await Rows(session, $"SELECT {expression}, id FROM t ORDER BY {expression}, id;");
        ordered.Count.ShouldBe(2);
        foreach (var row in ordered) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
        await session.ExecuteAsync($"INSERT INTO dest VALUES ({expression});", cancellationToken: CancellationToken.None);
        foreach (var row in await Rows(session, "SELECT v FROM dest;")) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
        await session.ExecuteAsync($"INSERT INTO dest SELECT {expression} FROM t;", cancellationToken: CancellationToken.None);
        foreach (var row in await Rows(session, "SELECT v FROM dest;")) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
        await session.ExecuteAsync($"UPDATE dest SET v = {expression};", cancellationToken: CancellationToken.None);
        var assigned = await Rows(session, "SELECT v FROM dest;");
        assigned.Count.ShouldBe(3);
        foreach (var row in assigned) { SqlT1ScalarTests.VerifyValue(row[0], expected); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 numeric functions: every numeric storage width is admitted")]
    [InlineData("TINYINT")]
    [InlineData("SMALLINT")]
    [InlineData("INTEGER")]
    [InlineData("BIGINT")]
    [InlineData("REAL")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("DECIMAL")]
    public async Task Select_NumericWidth_ShouldExecuteEveryNumericFunction(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("scalar-widths", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (n {type});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (2);", cancellationToken: CancellationToken.None);

        // Act / Assert
        var row = (await Rows(session, "SELECT ABS(n), ROUND(n), FLOOR(n), CEILING(n), CEIL(n), POWER(n, n), SQRT(n), MOD(n, n), SIGN(n), EXP(n), LN(n), LOG10(n) FROM t;")).ShouldHaveSingleItem();
        row.Length.ShouldBe(12);
        row.ShouldAllBe(value => value != null);
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: Unicode slicing, exact and approximate rounding and NULL rules are explicit")]
    public async Task Select_EdgeSemantics_ShouldRespectUnicodeRoundingAndNullRules()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("scalar-edge-semantics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act / Assert
        (await Rows(session, "SELECT LENGTH('😀'), SUBSTRING('A😀BC' FROM 0 FOR 3), LEFT('A😀BC', -1), RIGHT('A😀BC', -1), LPAD('😀', 0), RPAD('😀', -1), REVERSE('😀a'), POSITION('x' IN '😀'), STRPOS('😀a', 'a'), TRIM(BOTH '😀' FROM '😀a😀') FROM t;"))
            .ShouldBe([[1L, "A😀", "A😀B", "😀BC", "", "", "a😀", 0L, 2L, "a"]]);
        (await Rows(session, "SELECT SUBSTRING('x' FROM -9223372036854775807 FOR 1) FROM t;")).ShouldBe([[""]]);
        (await Rows(session, "SELECT ROUND(2.5), ROUND(-2.5), ROUND(125, -1), ROUND(-125, -1), ROUND(CAST(2.5 AS DOUBLE PRECISION)), ROUND(CAST(3.5 AS REAL)), POWER(2, -1), POWER(4, 0.5) FROM t;"))
            .ShouldBe([[3m, -3m, 130L, -130L, 2d, 4f, 0.5m, 2d]]);
        (await Rows(session, "SELECT CONCAT(NULL, NULL), CONCAT_WS(NULL, 'x'), CONCAT_WS('-', NULL, 'a', NULL), COALESCE(1, CAST('bad' AS INT)), NULLIF(NULL, 'x'), NULLIF('x', NULL), CONCAT(TRUE, DATE '2024-03-04') FROM t;"))
            .ShouldBe([["", null, "a", 1L, null, "x", "TRUE2024-03-04"]]);
        (await Rows(session, "SELECT EXTRACT(SECOND FROM TIME '12:34:56.125'), EXTRACT(EPOCH FROM INTERVAL '-1.5 seconds'), DATE_TRUNC('hour', TIMESTAMPTZ '2024-01-01 12:34:56+02:00') FROM t;"))
            .ShouldBe([[56.125m, -1.5m, new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero)]]);
        await using var nullIf = (await session.ExecuteAsync("SELECT NULLIF(1, 2) FROM t;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        nullIf.Columns[0].Type.ShouldBe(DatabaseType.Int64);
        (await SqlT1ScalarTests.ReadAsync(nullIf)).ShouldBe([[1L]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: CHECK rejects every stable and volatile built-in")]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_TIME")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("LOCALTIME")]
    [InlineData("LOCALTIMESTAMP")]
    [InlineData("NOW()")]
    [InlineData("GEN_RANDOM_UUID()")]
    public async Task Create_CheckWithClockOrUuid_ShouldRejectBeforeMutation(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("scalar-check-volatility", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"CREATE TABLE t (id INT CHECK ({expression} IS NOT NULL));", cancellationToken: CancellationToken.None).AsTask());
        failure.Message.ShouldContain(expression.Split('(')[0], Case.Sensitive);
        failure.Message.ShouldContain("IMMUTABLE", Case.Sensitive);
        (await Rows(session, "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 't';")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: immutable CHECK reloads by identity and volatile UUID never becomes a seek bound")]
    public async Task Execute_PersistedCheckAndUuid_ShouldPreserveDeterminismRules()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-t1-scalars", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("scalar-check-persist", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("CREATE TABLE t (code TEXT CONSTRAINT ck_code CHECK (CHAR_LENGTH(code) = 3), id UUID);", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("INSERT INTO t VALUES ('a😀b', GEN_RANDOM_UUID());", cancellationToken: CancellationToken.None);
                await session.ExecuteAsync("CREATE INDEX ix_uuid ON t(id);", cancellationToken: CancellationToken.None);
            }
            await using var reopened = SqlDatabaseEngine.Create("scalar-check-reopen", new() { RootPath = root });
            var opened = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var second = await opened.CreateSessionAsync(CancellationToken.None);

            // Act / Assert
            await second.ExecuteAsync("INSERT INTO t VALUES ('x😀z', GEN_RANDOM_UUID());", cancellationToken: CancellationToken.None);
            var failure = await SqlConstraintAssertions.ThrowAsync(() => second.ExecuteAsync("INSERT INTO t VALUES ('ab', GEN_RANDOM_UUID());", cancellationToken: CancellationToken.None).AsTask());
            failure.ConstraintKind.ShouldBe("CHECK");
            failure.ConstraintName.ShouldBe("ck_code");
            await using var listener = new InMemoryConnectionListener();
            await using var server = SqlDatabaseServer.Create(reopened, new() { Listener = listener });
            await server.StartAsync(CancellationToken.None);
            await using var client = new ProtocolTestClient(await listener.CreateFactory().ConnectAsync(listener.EndPoint, CancellationToken.None));
            await client.HandshakeAsync(database: "db");
            await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("INSERT INTO t VALUES ('ab', GEN_RANDOM_UUID())").Encode());
            var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Message.ShouldContain("CHECK constraint 'ck_code'", Case.Sensitive);
            (await Rows(second, "SELECT code FROM t WHERE id = GEN_RANDOM_UUID();")).ShouldBeEmpty();
            second.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
