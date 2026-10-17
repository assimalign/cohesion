using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTypedLiteralTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Binary results: every query route snapshots each output cell before returning")]
    [InlineData("SELECT X'00FF', binval(), @bytes, COALESCE(NULL, binval()) FROM dest ORDER BY id;")]
    [InlineData("SELECT b, binval(), @bytes, X'00FF' FROM dest ORDER BY id;")]
    [InlineData("SELECT b, binval(), @bytes, X'00FF' FROM dest WHERE b = X'00FF' ORDER BY id;")]
    [InlineData("SELECT b, binval(), @bytes, X'00FF' FROM tails ORDER BY id;")]
    [InlineData("SELECT appbin(id), binval(), @bytes, X'00FF' FROM dest ORDER BY id;")]
    [InlineData("SELECT b, binval(), @bytes, X'00FF' FROM dest GROUP BY b, id ORDER BY id;")]
    [InlineData("SELECT d.b, binval(), @bytes, X'00FF' FROM dest d JOIN tails t ON d.id = t.id ORDER BY d.id;")]
    [InlineData("SELECT (SELECT binval() FROM tails WHERE id = 1), binval(), @bytes, X'00FF' FROM dest ORDER BY id;")]
    [InlineData("SELECT X'00FF', binval(), @bytes, X'00FF' FROM INFORMATION_SCHEMA.TABLES;")]
    public async Task Select_BinaryResults_ShouldIsolateRowsFunctionsDefaultsAndParameters(string sql)
    {
        // Arrange
        byte[] functionValue = [0, 255];
        var builder = SqlDatabaseEngine.CreateBuilder("binary-ownership");
        builder.Functions.Add(SqlScalarFunction.Create<int, byte[]>("appbin", _ => functionValue, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION binval() RETURNS BINARY IMMUTABLE RETURN X'00FF';");
        await session.ExecuteAsync("CREATE TABLE dest (id INT, b BINARY DEFAULT binval());");
        await session.ExecuteAsync("INSERT INTO dest (id) VALUES (1), (2);");
        await session.ExecuteAsync("CREATE INDEX ix_binary ON dest(b);");
        await session.ExecuteAsync("CREATE TABLE tails (id INT);");
        await session.ExecuteAsync("INSERT INTO tails VALUES (1), (2);");
        await session.ExecuteAsync("ALTER TABLE tails ADD COLUMN b BINARY DEFAULT X'00FF';");
        byte[] parameter = [0, 255];
        var parameters = new Dictionary<string, object?> { ["bytes"] = parameter };

        // Act
        await using var result = (await session.ExecuteAsync(SqlQueryRequest.FromSql(sql, parameters), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        parameter[0] = 0x77;
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            for (int field = 0; field < row.FieldCount; field++)
            {
                // Assert: earlier cells and rows cannot change this cell, including GetBytes' backing array.
                row.GetValue(field).ShouldBeOfType<byte[]>().ShouldBe(new byte[] { 0, 255 }, sql);
                if (field % 2 == 0) { ((byte[])row.GetValue(field)!)[0] = 0x99; }
                else
                {
                    MemoryMarshal.TryGetArray(row.GetBytes(field), out var bytes).ShouldBeTrue();
                    bytes.Array![bytes.Offset] = 0x99;
                }
            }
            count++;
        }
        count.ShouldBe(2);

        // Assert: the returned bytes cannot alter cached function bodies, literal defaults or stored/indexed values.
        await session.ExecuteAsync("INSERT INTO dest (id) VALUES (3);");
        await session.ExecuteAsync("INSERT INTO tails (id) VALUES (3);");
        (await RowsAsync(session, "SELECT binval() = X'00FF', b = X'00FF', b = X'99FF' FROM dest ORDER BY id;"))
            .ShouldAllBe(row => Equals(row[0], true) && Equals(row[1], true) && Equals(row[2], false));
        (await RowsAsync(session, "SELECT b = X'00FF' FROM tails ORDER BY id;")).ShouldAllBe(row => Equals(row[0], true));
        (await RowsAsync(session, "SELECT id FROM dest WHERE b = X'00FF' ORDER BY id;")).Select(row => row[0]).ShouldBe(new object?[] { 1, 2, 3 });
        parameter[0].ShouldBe((byte)0x77);
        functionValue.ShouldBe(new byte[] { 0, 255 });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Binary DEFAULT: a mutated result cannot corrupt the next default-driven INSERT")]
    public async Task Insert_BinaryLiteralDefault_ShouldRetainCachedBytesAfterResultMutation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("binary-default", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE dest (id INT, b BINARY DEFAULT X'00FF');");
        await session.ExecuteAsync("INSERT INTO dest (id) VALUES (1);");

        // Act
        var returned = (await RowsAsync(session, "SELECT b FROM dest;")).ShouldHaveSingleItem()[0].ShouldBeOfType<byte[]>();
        returned[0] = 0x99;
        await session.ExecuteAsync("INSERT INTO dest (id) VALUES (2);");

        // Assert
        (await RowsAsync(session, "SELECT b = X'00FF', b = X'99FF' FROM dest ORDER BY id;"))
            .ShouldAllBe(row => Equals(row[0], true) && Equals(row[1], false));
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Empty binary results: even empty cells have separate arrays")]
    public async Task Select_EmptyBinary_ShouldOwnEachOutputArray()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("empty-binary", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);");
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2);");

        // Act
        var rows = await RowsAsync(session, "SELECT X'', X'' FROM t;");

        // Assert
        var values = rows.SelectMany(row => row).Select(value => value.ShouldBeOfType<byte[]>()).ToArray();
        values.Length.ShouldBe(4);
        for (int i = 0; i < values.Length; i++)
        {
            values[i].ShouldBeEmpty();
            values[i].ShouldNotBeSameAs(Array.Empty<byte>());
            for (int j = 0; j < i; j++) { values[i].ShouldNotBeSameAs(values[j]); }
        }
    }

    public static IEnumerable<object[]> Literals() =>
    [
        ["DATE", "DATE '2024-01-01'", DatabaseType.Date, new DateOnly(2024, 1, 1)],
        ["TIME", "TIME '10:20:30.1234567'", DatabaseType.Time, new TimeOnly(10, 20, 30).Add(TimeSpan.FromTicks(1234567))],
        ["TIMESTAMP", "TIMESTAMP '2024-01-01 10:00:00'", DatabaseType.DateTime, new DateTime(2024, 1, 1, 10, 0, 0)],
        ["TIMESTAMPTZ", "TIMESTAMPTZ '2024-01-01 12:00:00+02:00'", DatabaseType.DateTimeOffset, new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))],
        ["INTERVAL", "INTERVAL '1' DAY", DatabaseType.TimeSpan, TimeSpan.FromDays(1)],
        ["INTERVAL", "INTERVAL '1 day 02:03:04'", DatabaseType.TimeSpan, new TimeSpan(1, 2, 3, 4)],
        ["INTERVAL", "INTERVAL '1 02:03:04'", DatabaseType.TimeSpan, new TimeSpan(1, 2, 3, 4)],
        ["INTERVAL", "INTERVAL '02:03:04'", DatabaseType.TimeSpan, new TimeSpan(2, 3, 4)],
        ["INTERVAL", "INTERVAL '-1 day -02:03:04'", DatabaseType.TimeSpan, -new TimeSpan(1, 2, 3, 4)],
        ["INTERVAL", "INTERVAL '-1 02:03:04'", DatabaseType.TimeSpan, new TimeSpan(-1, 2, 3, 4)],
        ["INTERVAL", "INTERVAL '1 02:03:04.1234567'", DatabaseType.TimeSpan, new TimeSpan(1, 2, 3, 4).Add(TimeSpan.FromTicks(1234567))],
        ["INTERVAL", "INTERVAL 'P1DT2H'", DatabaseType.TimeSpan, new TimeSpan(1, 2, 0, 0)],
        ["INTERVAL", "INTERVAL '10675199 02:48:05.4775807'", DatabaseType.TimeSpan, TimeSpan.MaxValue],
        ["INTERVAL", "INTERVAL '-10675199 -02:48:05.4775808'", DatabaseType.TimeSpan, TimeSpan.MinValue],
        ["BINARY", "X'00ff'", DatabaseType.Binary, new byte[] { 0, 255 }],
        ["TEXT", "N'héllo'", DatabaseType.String, "héllo"],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Typed literals: INSERT, DEFAULT, UPDATE, grouping and parameters retain values and metadata")]
    [MemberData(nameof(Literals))]
    public async Task Execute_TypedLiteral_ShouldUseSameValueAcrossStorageAndDefaults(string type, string literal, DatabaseType metadata, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("typed-literals", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v {type} DEFAULT {literal});");

        // Act
        await session.ExecuteAsync($"INSERT INTO t VALUES (1, {literal}), (2, DEFAULT);");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (3);");
        await session.ExecuteAsync($"UPDATE t SET v = {literal} WHERE id = 1;");
        await session.ExecuteAsync("UPDATE t SET v = DEFAULT WHERE id = 3;");
        await using var projected = (await session.ExecuteAsync($"SELECT {literal} FROM t WHERE id = 1;")).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        projected.Columns.Single().Type.ShouldBe(metadata);
        (await ReadAsync(projected)).ShouldHaveSingleItem().ShouldBe(new object?[] { expected });
        (await RowsAsync(session, $"SELECT id FROM t WHERE v = {literal} ORDER BY id;")).Select(r => r[0]).ShouldBe(new object?[] { 1, 2, 3 });
        (await RowsAsync(session, "SELECT v, COUNT(*) FROM t GROUP BY v ORDER BY v;")).ShouldHaveSingleItem().ShouldBe(new object?[] { expected, 3L });
        var parameters = new Dictionary<string, object?> { ["value"] = expected };
        await using var parameterResult = (await session.ExecuteAsync(SqlQueryRequest.FromSql("SELECT id FROM t WHERE v = @value ORDER BY id;", parameters), CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        (await ReadAsync(parameterResult)).Select(r => r[0]).ShouldBe(new object?[] { 1, 2, 3 });
        await session.ExecuteAsync($"CREATE FUNCTION pick(v {type}) RETURNS INT IMMUTABLE RETURN 1;");
        await session.ExecuteAsync("CREATE FUNCTION pick(v BOOLEAN) RETURNS INT IMMUTABLE RETURN 2;");
        await using var functions = (await session.ExecuteAsync($"SELECT pick({literal}) = 1, COALESCE(NULL, {literal}) FROM t WHERE id = 1;")).ShouldBeAssignableTo<DatabaseResultSet>();
        functions.Columns[1].Type.ShouldBe(metadata);
        (await ReadAsync(functions)).ShouldHaveSingleItem().ShouldBe(new object?[] { true, expected });
        await session.ExecuteAsync($"CREATE TABLE added (id INT); ");
        await session.ExecuteAsync("INSERT INTO added VALUES (1);");
        await session.ExecuteAsync($"ALTER TABLE added ADD COLUMN v {type} NOT NULL DEFAULT {literal};");
        (await RowsAsync(session, "SELECT v FROM added;")).ShouldHaveSingleItem().ShouldBe(new object?[] { expected });
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Typed defaults: compiled schema validation retains literal types")]
    [MemberData(nameof(Literals))]
    public async Task ApplySchema_TypedLiteralDefault_ShouldValidateAndSupplyValue(string type, string literal, DatabaseType metadata, object expected)
    {
        // Arrange
        var schema = new SqlCompiledSchema(SqlCompiledSchema.CurrentFormat, "db", false, [],
            [new CompiledSchemaTable("typed", "Tests.Typed", [new("id", DatabaseType.Int32, false), new("v", metadata, false, DefaultLiteral: literal)],
                new CompiledSchemaKey("pk_typed", ["id"]), [], [])], []);
        await using var engine = SqlDatabaseEngine.Create("typed-schema", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");

        // Act
        await database.ApplySchemaAsync(schema, cancellationToken: CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO typed (id) VALUES (1);");

        // Assert
        (await RowsAsync(session, "SELECT v FROM typed;")).ShouldHaveSingleItem().ShouldBe(new object?[] { expected }, type);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Typed literals: indexed DATE and TIMESTAMP predicates match scans")]
    [InlineData("DATE", "DATE '2024-01-01'", "DATE '2024-01-02'", "DATE '2024-01-03'")]
    [InlineData("TIMESTAMP", "TIMESTAMP '2024-01-01 10:00'", "TIMESTAMP '2024-01-02 10:00'", "TIMESTAMP '2024-01-03 10:00'")]
    public async Task Select_TypedSeekBound_ShouldMatchScan(string type, string first, string middle, string last)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("typed-seek", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v {type});");
        await session.ExecuteAsync($"INSERT INTO t VALUES (1, {first}), (2, {middle}), (3, {last});");
        string[] predicates = [$"v = {middle}", $"v >= {middle}", $"v > {first} AND v <= {last}"];
        var scans = new List<object?[][]>();
        foreach (string predicate in predicates) { scans.Add((await RowsAsync(session, $"SELECT id FROM t WHERE {predicate} ORDER BY id;")).ToArray()); }
        await session.ExecuteAsync("CREATE INDEX ix_v ON t(v);");

        // Act / Assert
        for (int i = 0; i < predicates.Length; i++)
        {
            var rows = await RowsAsync(session, $"SELECT id FROM t WHERE {predicates[i]} ORDER BY id;");
            session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_v");
            rows.Select(r => r[0]).ShouldBe(scans[i].Select(r => r[0]));
        }
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM t WHERE v = '2024-01-01';"));
        if (type == "DATE") { failure.Message.ShouldContain("DATE '", Case.Sensitive); }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal identity: assignment and mixed comparisons use UTC without a session zone")]
    public async Task Execute_TimestampAndInstant_ShouldNormalizeAtEveryBoundary()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("typed-temporal", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, ts TIMESTAMP, tz TIMESTAMPTZ, d DATE, tm TIME);");

        // Act
        await session.ExecuteAsync("INSERT INTO t (id, ts, tz) VALUES (1, TIMESTAMPTZ '2024-01-01 12:00:00+02:00', TIMESTAMP '2024-01-01 10:00:00');");
        var parameters = new Dictionary<string, object?> { ["instant"] = new DateTimeOffset(2024, 1, 2, 1, 30, 0, TimeSpan.FromHours(2)), ["clock"] = new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Local) };
        await session.ExecuteAsync(SqlQueryRequest.FromSql("INSERT INTO t (id, ts, tz, d, tm) VALUES (2, @instant, @clock, @instant, @instant);", parameters), CancellationToken.None);
        await session.ExecuteAsync("UPDATE t SET ts = TIMESTAMPTZ '2024-01-01 12:00:00+02:00', tz = TIMESTAMP '2024-01-01 10:00:00' WHERE id = 1;");
        await session.ExecuteAsync(SqlQueryRequest.FromSql("UPDATE t SET ts = @instant, tz = @clock WHERE id = 2;", parameters), CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT ts, tz, d, tm FROM t ORDER BY id;");

        // Assert
        rows[0][0].ShouldBeOfType<DateTime>().Kind.ShouldBe(DateTimeKind.Unspecified);
        rows[0][0].ShouldBe(new DateTime(2024, 1, 1, 10, 0, 0));
        rows[0][1].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        rows[1][0].ShouldBe(new DateTime(2024, 1, 1, 23, 30, 0));
        rows[1][0].ShouldBeOfType<DateTime>().Kind.ShouldBe(DateTimeKind.Unspecified);
        rows[1][1].ShouldBeOfType<DateTimeOffset>().Offset.ShouldBe(TimeSpan.Zero);
        rows[1][2].ShouldBe(new DateOnly(2024, 1, 1));
        rows[1][3].ShouldBe(new TimeOnly(23, 30));
        (await RowsAsync(session, "SELECT TIMESTAMPTZ '2024-01-01 12:00:00+02:00' = TIMESTAMPTZ '2024-01-01 10:00:00', TIMESTAMP '2024-01-01 10:00:00' = TIMESTAMPTZ '2024-01-01 12:00:00+02:00' FROM t WHERE id = 1;"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { true, true });
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Typed defaults: canonical text survives reopen and plain-string defaults keep resolving")]
    public async Task Default_PersistedTypedAndPlainString_ShouldReopenAndRenderResolvedValues()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-typed-defaults", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("typed-defaults", new SqlDatabaseEngineOptions { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db");
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await session.ExecuteAsync("CREATE TABLE t (id INT, d DATE DEFAULT '2024-01-01', loose DATE DEFAULT '2024-1-1', b BINARY DEFAULT X'00ff', i INTERVAL DEFAULT INTERVAL '1' DAY, ts TIMESTAMP DEFAULT TIMESTAMP '2024-01-01 10:00:00', tm TIME DEFAULT TIME '10:00', tz TIMESTAMPTZ DEFAULT TIMESTAMPTZ '2024-01-01 12:00+02:00');");
            }

            // Act
            await using var reopened = SqlDatabaseEngine.Create("typed-defaults", new SqlDatabaseEngineOptions { RootPath = root });
            var db = await reopened.OpenDatabaseAsync("db");
            await using var opened = await db.CreateSessionAsync(CancellationToken.None);
            await opened.ExecuteAsync("INSERT INTO t (id) VALUES (1);");
            await opened.ExecuteAsync("INSERT INTO t VALUES (2, DEFAULT, DEFAULT, DEFAULT, DEFAULT, DEFAULT, DEFAULT, DEFAULT);");
            var defaults = await RowsAsync(opened, "SELECT COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 't' ORDER BY ORDINAL_POSITION;");

            // Assert
            defaults.Select(r => r[0]).ShouldBe(new object?[] { null, "DATE '2024-01-01'", "DATE '2024-01-01'", "X'00FF'", "INTERVAL 'PT86400S'", "TIMESTAMP '2024-01-01 10:00:00'", "TIME '10:00:00'", "TIMESTAMPTZ '2024-01-01 12:00:00.0000000+02:00'" });
            var values = await RowsAsync(opened, "SELECT d, loose, b, i, ts, tm, tz FROM t ORDER BY id;");
            values.Count.ShouldBe(2);
            foreach (var row in values) { row[0].ShouldBe(new DateOnly(2024, 1, 1)); row[2].ShouldBe(new byte[] { 0, 255 }); row[3].ShouldBe(TimeSpan.FromDays(1)); }
            for (int index = 1; index < defaults.Count; index++)
            {
                string text = (string)defaults[index][0]!;
                new SqlQueryParser().Parse($"SELECT {text};").Diagnostics.ShouldBeEmpty();
                (await RowsAsync(opened, $"SELECT {text} FROM t WHERE id = 1;")).ShouldHaveSingleItem().ShouldBe(new object?[] { values[0][index - 1] });
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Defaults: signed numerics store exactly and failed typed declarations leave tables intact")]
    [InlineData("-1", -1L)]
    [InlineData("- 1", -1L)]
    [InlineData("+7", 7L)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public async Task Default_SignedNumeric_ShouldBackfillAndRejectMismatchedTypes(string literal, long expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("signed-defaults", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v BIGINT DEFAULT {literal});");

        // Act / Assert
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1);");
        await session.ExecuteAsync($"ALTER TABLE t ADD COLUMN added BIGINT NOT NULL DEFAULT {literal};");
        (await RowsAsync(session, "SELECT v, added FROM t;")).ShouldHaveSingleItem().ShouldBe(new object?[] { expected, expected });
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE t ADD COLUMN wrong DATE DEFAULT TIME '10:00';"));
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("ALTER TABLE t ADD COLUMN wrong TEXT DEFAULT -'x';"));
        (await RowsAsync(session, "SELECT * FROM t;")).ShouldHaveSingleItem().Length.ShouldBe(3);
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("CREATE TABLE failed (d DATE DEFAULT TIMESTAMP '2024-01-01 10:00');"));
        database.Catalog.TryGetTable("dbo", "failed", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Defaults: signed exponent defaults use the same backfill and insertion value")]
    public async Task Default_SignedExponent_ShouldBackfillAndInsertExactConvertedValue()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("signed-exponent", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, v DOUBLE DEFAULT -1.5e3);");
        await session.ExecuteAsync("INSERT INTO t (id) VALUES (1);");

        // Act
        await session.ExecuteAsync("ALTER TABLE t ADD COLUMN added DOUBLE DEFAULT -1.5e3;");
        await session.ExecuteAsync("INSERT INTO t VALUES (2, DEFAULT, DEFAULT);");

        // Assert
        var rows = await RowsAsync(session, "SELECT v, added FROM t ORDER BY id;");
        rows.Count.ShouldBe(2);
        foreach (var row in rows) { row.ShouldBe(new object?[] { -1500d, -1500d }); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
