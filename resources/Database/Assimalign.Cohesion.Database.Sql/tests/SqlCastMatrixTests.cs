using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlCastMatrixTests
{
    // One supported-pair table, in the same order as the DIALECT matrix. Identity is universal.
    private static readonly string[] _names =
        ["BOOLEAN", "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "REAL", "DOUBLE", "NUMERIC", "TEXT", "BYTEA",
         "DATE", "TIME", "TIMESTAMP", "TIMESTAMPTZ", "INTERVAL", "UUID", "JSON", "JSONB", "CHAR(100)"];
    private static readonly DatabaseType[] _types =
        [DatabaseType.Boolean, DatabaseType.Int8, DatabaseType.Int16, DatabaseType.Int32, DatabaseType.Int64,
         DatabaseType.Float32, DatabaseType.Float64, DatabaseType.Decimal, DatabaseType.String, DatabaseType.Binary,
         DatabaseType.Date, DatabaseType.Time, DatabaseType.DateTime, DatabaseType.DateTimeOffset, DatabaseType.TimeSpan,
         DatabaseType.Guid, DatabaseType.Json, DatabaseType.JsonBinary, DatabaseType.Character];
    private static readonly int[][] _targets =
    [
        [8, 18], [2, 3, 4, 5, 6, 7, 8, 18], [1, 3, 4, 5, 6, 7, 8, 18],
        [1, 2, 4, 5, 6, 7, 8, 18], [1, 2, 3, 5, 6, 7, 8, 18],
        [1, 2, 3, 4, 6, 7, 8, 18], [1, 2, 3, 4, 5, 7, 8, 18], [1, 2, 3, 4, 5, 6, 8, 18],
        [0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18],
        [8, 18], [8, 12, 13, 18], [8, 14, 18], [8, 10, 11, 13, 18], [8, 10, 11, 12, 18],
        [8, 11, 18], [8, 18], [8, 17, 18], [8, 16, 18],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
    ];

    public static IEnumerable<object[]> Pairs()
    {
        for (int source = 0; source < _names.Length; source++)
        {
            for (int target = 0; target < _names.Length; target++)
            {
                yield return [source, target, source == target || _targets[source].Contains(target)];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: all 19 by 19 pairs retain SQL identity on values, NULL and empty input")]
    [MemberData(nameof(Pairs))]
    public async Task Cast_EverySharedTypePair_ShouldMatchTheDialect(int source, int target, bool supported)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-matrix", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, v {_names[source]});", cancellationToken: CancellationToken.None);
        object sourceValue = source is 8 or 18 ? Input(target) : Value(source);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, @v), (2, NULL);",
            new Dictionary<string, object?> { ["v"] = sourceValue }, cancellationToken: CancellationToken.None);
        string expression = $"CAST(v AS {_names[target]})";

        // Act / Assert
        if (!supported)
        {
            foreach (string predicate in new[] { "id = 1", "id = 2", "id < 0" })
            {
                var error = await Should.ThrowAsync<DatabaseException>(async () =>
                    await session.ExecuteAsync($"SELECT {expression} FROM t WHERE {predicate};", cancellationToken: CancellationToken.None));
                error.DiagnosticDetail("CS02011").ShouldStartWith("Cannot CAST type", Case.Sensitive);
                error.Message.ShouldContain($"Cannot CAST type {DiagnosticName(source)} to {DiagnosticName(target)}", Case.Sensitive);
            }
            return;
        }
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t ORDER BY id;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        result.Columns[0].Type.ShouldBe(_types[target]);
        var rows = await ReadAsync(result);
        rows.Count.ShouldBe(2);
        rows[0].ShouldBe(Expected(source, target, sourceValue));
        rows[1].ShouldBeNull();
        await using var empty = (await session.ExecuteAsync($"SELECT {expression} FROM t WHERE id < 0;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        empty.Columns[0].Type.ShouldBe(_types[target]);
        (await ReadAsync(empty)).ShouldBeEmpty();
        await using var nullOperand = (await session.ExecuteAsync($"SELECT CAST(NULL AS {_names[target]}) FROM t WHERE id = 1;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        nullOperand.Columns[0].Type.ShouldBe(_types[target]);
        (await ReadAsync(nullOperand)).ShouldHaveSingleItem().ShouldBeNull();
    }

    private static string DiagnosticName(int index) => index switch { 9 => "BINARY", 18 => "CHAR", _ => _names[index] };
    private static object Value(int index) => index switch
    {
        0 => true, 1 => (sbyte)1, 2 => (short)1, 3 => 1, 4 => 1L, 5 => 1f, 6 => 1d, 7 => 1m,
        8 => "value", 9 => new byte[] { 0, 255 }, 10 => new DateOnly(2024, 1, 1),
        11 => new TimeOnly(1, 2, 3), 12 => new DateTime(2024, 1, 1, 1, 2, 3),
        13 => new DateTimeOffset(2024, 1, 1, 1, 2, 3, TimeSpan.FromHours(2)), 14 => new TimeSpan(1, 2, 3),
        15 => Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
        16 => " {\"v\":\"雪\"} ", 17 => Encoding.UTF8.GetBytes(" {\"v\":\"雪\"} "), 18 => "value".PadRight(100),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
    private static string Input(int target) => target switch
    {
        0 => "TRUE", >= 1 and <= 7 => "1", 8 or 18 => "value", 9 => "\\x00ff", 10 => "2024-01-01",
        11 or 14 => "01:02:03", 12 => "2024-01-01 01:02:03", 13 => "2024-01-01 01:02:03+02:00",
        15 => "00112233-4455-6677-8899-aabbccddeeff", 16 or 17 => " {\"v\":\"雪\"} ",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
    private static object Expected(int source, int target, object value)
    {
        if (source == target) { return source == 18 ? ((string)value).PadRight(100) : value; }
        if (target is 8 or 18)
        {
            string text = source switch
            {
                0 => "TRUE", >= 1 and <= 6 => "1", 7 => "1.0", 8 or 18 => (string)value,
                9 => "\\x00ff", 10 => "2024-01-01", 11 or 14 => "01:02:03",
                12 => "2024-01-01 01:02:03", 13 => "2024-01-01 01:02:03+02:00",
                15 => "00112233-4455-6677-8899-aabbccddeeff", 16 => (string)value,
                17 => Encoding.UTF8.GetString((byte[])value), _ => throw new ArgumentOutOfRangeException(nameof(source)),
            };
            return target == 18 ? text.PadRight(100) : text;
        }
        if (source == 18 && target == 16) { return ((string)value).TrimEnd(' '); }
        if (source == 18 && target == 17) { return Encoding.UTF8.GetBytes(((string)value).TrimEnd(' ')); }
        if (source is 8 or 18) { return Value(target); }
        if (source >= 1 && source <= 7) { return Value(target); }
        if (source == 10 && target == 12) { return new DateTime(2024, 1, 1); }
        if (source == 10 && target == 13) { return new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero); }
        if (source == 12 && target == 13) { return new DateTimeOffset(2024, 1, 1, 1, 2, 3, TimeSpan.Zero); }
        if (source == 13)
        {
            return target switch
            {
                10 => new DateOnly(2023, 12, 31), 11 => new TimeOnly(23, 2, 3),
                12 => new DateTime(2023, 12, 31, 23, 2, 3), _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
        }
        return Value(target);
    }

    public static IEnumerable<object[]> FamilyConversions() =>
    [
        ["SELECT CAST(CAST('1.25' AS DOUBLE PRECISION) AS NUMERIC) FROM t WHERE id = 1;", 1.25m, DatabaseType.Decimal],
        ["SELECT CAST(DATE '2024-01-01' AS TIMESTAMP) FROM t WHERE id = 1;", new DateTime(2024, 1, 1), DatabaseType.DateTime],
        ["SELECT CAST(TIME '01:02:03' AS INTERVAL) FROM t WHERE id = 1;", new TimeSpan(1, 2, 3), DatabaseType.TimeSpan],
        ["SELECT CAST('12' AS INTERVAL) FROM t WHERE id = 1;", TimeSpan.FromSeconds(12), DatabaseType.TimeSpan],
        ["SELECT CAST(CAST('00112233-4455-6677-8899-aabbccddeeff' AS UNIQUEIDENTIFIER) AS TEXT) FROM t WHERE id = 1;", "00112233-4455-6677-8899-aabbccddeeff", DatabaseType.String],
        ["SELECT CAST('\\x00ff' AS BYTEA) FROM t WHERE id = 1;", new byte[] { 0, 255 }, DatabaseType.Binary],
        ["SELECT CAST(CAST('{\"雪\":1}' AS JSONB) AS JSON) FROM t WHERE id = 1;", "{\"雪\":1}", DatabaseType.Json],
    ];

    public static IEnumerable<object[]> RoundTrips()
    {
        yield return ["BOOLEAN", true];
        yield return ["BOOLEAN", false];
        foreach (var value in new[] { sbyte.MinValue, sbyte.MaxValue }) { yield return ["TINYINT", value]; }
        foreach (var value in new[] { short.MinValue, short.MaxValue }) { yield return ["SMALLINT", value]; }
        foreach (var value in new[] { int.MinValue, int.MaxValue }) { yield return ["INTEGER", value]; }
        foreach (var value in new[] { long.MinValue, long.MaxValue }) { yield return ["BIGINT", value]; }
        foreach (var value in new[] { decimal.MinValue, decimal.MaxValue }) { yield return ["NUMERIC", value]; }
        yield return ["TEXT", "  雪  "];
        yield return ["CHAR(4)", "雪   "];
        foreach (var value in new[] { DateOnly.MinValue, DateOnly.MaxValue }) { yield return ["DATE", value]; }
        foreach (var value in new[] { TimeOnly.MinValue, TimeOnly.MaxValue }) { yield return ["TIME", value]; }
        foreach (var value in new[] { DateTime.MinValue, DateTime.MaxValue }) { yield return ["TIMESTAMP", value]; }
        foreach (var value in new[] { DateTimeOffset.MinValue, DateTimeOffset.MaxValue,
            new DateTimeOffset(2024, 1, 1, 1, 2, 3, TimeSpan.FromHours(-5)).AddTicks(1234567),
            new DateTimeOffset(2024, 1, 1, 1, 2, 3, TimeSpan.FromMinutes(330)) }) { yield return ["TIMESTAMPTZ", value]; }
        foreach (var value in new[] { TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.FromTicks(-1), TimeSpan.Zero }) { yield return ["INTERVAL", value]; }
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0f, float.Epsilon, -float.Epsilon, float.MaxValue }) { yield return ["REAL", value]; }
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0d, double.Epsilon, -double.Epsilon, double.MaxValue }) { yield return ["DOUBLE PRECISION", value]; }
        yield return ["UUID", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff")];
        yield return ["BYTEA", new byte[] { 0, 255 }];
        yield return ["BYTEA", new byte[0]];
        yield return ["JSON", " {\"雪\": [1, true, null]} "];
        yield return ["JSONB", Encoding.UTF8.GetBytes(" {\"雪\": [1, true, null]} ")];
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: canonical text round trips edge values and matches concatenation")]
    [MemberData(nameof(RoundTrips))]
    public async Task Cast_EdgeValueThroughText_ShouldRoundTrip(string type, object value)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-roundtrip", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (v {type});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (@v);", new Dictionary<string, object?> { ["v"] = value }, cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT CAST(v AS TEXT), CAST(CAST(v AS TEXT) AS {type}), v || '' FROM t;", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var row = (await RowsAsync(result)).ShouldHaveSingleItem();

        // Assert
        row[1].ShouldBe(value);
        row[2].ShouldBe(row[0]);
        if (value is float single && !float.IsNaN(single)) { BitConverter.SingleToInt32Bits((float)row[1]!).ShouldBe(BitConverter.SingleToInt32Bits(single)); }
        if (value is double number && !double.IsNaN(number)) { BitConverter.DoubleToInt64Bits((double)row[1]!).ShouldBe(BitConverter.DoubleToInt64Bits(number)); }
        if (value is DateTimeOffset instant) { ((DateTimeOffset)row[1]!).Offset.ShouldBe(instant.Offset); }
        if (value is DateTime) { ((DateTime)row[1]!).Kind.ShouldBe(DateTimeKind.Unspecified); }
        if (value is float zero && zero == 0 && float.IsNegative(zero) || value is double doubleZero && doubleZero == 0 && double.IsNegative(doubleZero))
        {
            row[0].ShouldBe("-0");
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: invalid strict text, exactness and range fail as database errors")]
    [InlineData("CAST('2024-02-30' AS DATE)", "CS02048")]
    [InlineData("CAST('1e400' AS DOUBLE PRECISION)", "CS02002")]
    [InlineData("CAST('1e-400' AS DOUBLE PRECISION)", "CS02002")]
    [InlineData("CAST('1e-50' AS REAL)", "CS02002")]
    [InlineData("CAST(CAST('1.5' AS DOUBLE PRECISION) AS INT)", "CS02011")]
    [InlineData("CAST(CAST('1e300' AS DOUBLE PRECISION) AS REAL)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS DOUBLE PRECISION) AS DECIMAL)", "CS02022")]
    [InlineData("CAST(CAST('NaN' AS REAL) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS REAL) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS REAL) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS REAL) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS REAL) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS REAL) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS REAL) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS REAL) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS REAL) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS REAL) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS REAL) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS REAL) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS DOUBLE) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS DOUBLE) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS DOUBLE) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('NaN' AS DOUBLE) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS DOUBLE) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS DOUBLE) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS DOUBLE) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('Infinity' AS DOUBLE) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS DOUBLE) AS TINYINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS DOUBLE) AS SMALLINT)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS DOUBLE) AS INTEGER)", "CS02002")]
    [InlineData("CAST(CAST('-Infinity' AS DOUBLE) AS BIGINT)", "CS02002")]
    [InlineData("CAST(CAST('1e-29' AS DOUBLE) AS NUMERIC)", "CS02002")]
    [InlineData("CAST(INTERVAL '25 hours' AS TIME)", "CS02011")]
    [InlineData("CAST(INTERVAL '-0.0000001 seconds' AS TIME)", "CS02011")]
    [InlineData("CAST('1 year' AS INTERVAL)", "CS02047")]
    [InlineData("CAST('12.34.56' AS INTERVAL)", "CS02047")]
    [InlineData("CAST('2024-01-01 00:00:00+02:00' AS TIMESTAMP)", "CS02047")]
    [InlineData("CAST('00:00:00.12345678' AS TIME)", "CS02047")]
    [InlineData("CAST('\\xZZ' AS BYTEA)", "CS02022")]
    [InlineData("CAST('\\x0' AS BYTEA)", "CS02022")]
    [InlineData("CAST('a\\b' AS BYTEA)", "CS02022")]
    [InlineData("CAST('{bad' AS JSON)", "CS02022")]
    [InlineData("CAST('true false' AS JSONB)", "CS02022")]
    [InlineData("CAST('00112233-4455-6677-8899-aabbccddeefg' AS UUID)", "CS02022")]
    [InlineData("CAST('\\x00ff' AS VARBINARY(1))", "CS02011")]
    public async Task Cast_InvalidInput_ShouldReturnConversionFailure(string expression, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-invalid", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);
        var request = SqlQueryRequest.FromSql($"SELECT {expression} FROM t;");
        var cast = ((SqlSelectExpression)request.Statement.SqlExpression).Columns.Single().Expression;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, cancellationToken: CancellationToken.None));

        // Assert
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldBe(cast.Location!.Start);
        diagnostic.End.ShouldBe(cast.Location.End);
        error.Message.ShouldContain("CAST", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: BYTEA preserves UTF-8 payload whitespace and keeps hex input rules")]
    [InlineData(" a ", "206120")]
    [InlineData("   ", "202020")]
    [InlineData("\t\r\n", "090D0A")]
    [InlineData(" \t雪😀\r\n ", "2009E99BAAF09F98800D0A20")]
    [InlineData("\\x", "")]
    [InlineData(" \\x00fF ", "00FF")]
    [InlineData("\\x202020", "202020")]
    public async Task Cast_BinaryText_ShouldPreservePayload(string text, string hex)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-binary-text", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync("SELECT CAST(@v AS BYTEA) FROM t;",
            new Dictionary<string, object?> { ["v"] = text }, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await ReadAsync(result)).ShouldHaveSingleItem().ShouldBe(Convert.FromHexString(hex));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: NUMERIC and TEXT round directly to REAL at binary midpoints")]
    [InlineData("1.000000059604644775390625", 1f)]
    [InlineData("1.0000000596046448", 1.0000001f)]
    [InlineData("-1.0000000596046448", -1.0000001f)]
    [InlineData("1.000000059604644775390626", 1.0000001f)]
    [InlineData("-1.000000059604644775390626", -1.0000001f)]
    public async Task Cast_RealMidpoint_ShouldRoundOnce(string text, float expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-real-rounding", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (v NUMERIC);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO t VALUES ({text});", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT CAST({text} AS REAL), CAST('{text}' AS REAL), CAST(v AS REAL) FROM t;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        var row = (await RowsAsync(result)).ShouldHaveSingleItem();
        foreach (var value in row) { BitConverter.SingleToInt32Bits(value.ShouldBeOfType<float>()).ShouldBe(BitConverter.SingleToInt32Bits(expected)); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: INTERVAL emits postgres day and clock fields and round trips")]
    [InlineData("1 day 02:03:04", "1 day 02:03:04")]
    [InlineData("1 day", "1 day")]
    [InlineData("2 days", "2 days")]
    [InlineData("-1 day", "-1 days")]
    [InlineData("-1 day -02:03:04", "-1 days -02:03:04")]
    [InlineData("-2 days -02:03:04.1234567", "-2 days -02:03:04.1234567")]
    [InlineData("2 days -02:03:04", "1 day 21:56:56")]
    [InlineData("-2 days +02:03:04", "-1 days -21:56:56")]
    [InlineData("1 day -25 hours", "-01:00:00")]
    [InlineData("0 seconds", "00:00:00")]
    [InlineData("-0.0000001 seconds", "-00:00:00.0000001")]
    public async Task Cast_IntervalText_ShouldUsePostgresStyle(string input, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-interval-text", new());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (v INTERVAL);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO t VALUES (INTERVAL '{input}');", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync("SELECT CAST(v AS TEXT), v || '', CAST(CAST(v AS TEXT) AS INTERVAL) FROM t;",
            cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        var row = (await RowsAsync(result)).ShouldHaveSingleItem();
        row.ShouldBe(new object?[] { expected, expected, SqlLiteralText.ParseInterval(input) });
        SqlLiteralText.ParseInterval(expected).ShouldBe(SqlLiteralText.ParseInterval(input));
    }

    public static IEnumerable<object[]> Aliases()
    {
        string[][] aliases =
        [
            ["BOOLEAN", "BOOL"], ["TINYINT"], ["SMALLINT", "INT2"], ["INT", "INTEGER", "INT4"], ["BIGINT", "INT8"],
            ["REAL", "FLOAT4", "FLOAT(24)"], ["DOUBLE", "DOUBLE PRECISION", "FLOAT8", "FLOAT", "FLOAT(25)"], ["NUMERIC", "DECIMAL"],
            ["TEXT", "VARCHAR", "NVARCHAR", "CHARACTER VARYING", "CHAR VARYING", "NCHAR VARYING", "NATIONAL CHAR VARYING", "NATIONAL CHARACTER VARYING", "NVARCHAR(MAX)"],
            ["BYTEA", "BINARY", "VARBINARY", "BLOB", "BINARY VARYING", "VARBINARY(MAX)"], ["DATE"], ["TIME", "TIME WITHOUT TIME ZONE"],
            ["TIMESTAMP", "TIMESTAMP WITHOUT TIME ZONE", "DATETIME", "DATETIME2"], ["TIMESTAMPTZ", "TIMESTAMP WITH TIME ZONE", "DATETIMEOFFSET"],
            ["INTERVAL"], ["UUID", "GUID", "UNIQUEIDENTIFIER"], ["JSON"], ["JSONB"], ["CHAR", "CHARACTER", "NCHAR", "NATIONAL CHAR", "NATIONAL CHARACTER"],
        ];
        for (int target = 0; target < aliases.Length; target++)
        {
            foreach (string alias in aliases[target]) { yield return [alias, target]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: every built-in alias and multi-word target spelling executes")]
    [MemberData(nameof(Aliases))]
    public async Task Cast_EveryTargetSpelling_ShouldExecute(string alias, int target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-aliases", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT CAST(@v AS {alias}) FROM t;",
            new Dictionary<string, object?> { ["v"] = Input(target) }, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns[0].Type.ShouldBe(_types[target]);
        (await ReadAsync(result)).ShouldHaveSingleItem().ShouldBe(target is 8 ? "value" : target is 18 ? "v" : Value(target));
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - CAST: JSON validation preserves text and enforces depth 128")]
    [InlineData("JSON")]
    [InlineData("JSONB")]
    public async Task Cast_JsonText_ShouldValidateWithoutNormalization(string type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-json-validation", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);
        string deep = new string('[', 128) + "1" + new string(']', 128);
        string[] valid = [" 1 ", "true", "null", "\"雪\"", " {\"b\": 2, \"a\":1, \"a\":3} ", deep];
        string[] invalid = ["", "{bad", "[1,]", "/*comment*/1", "true false", "[" + deep + "]", "\"\ud800\""];

        // Act / Assert
        foreach (string text in valid)
        {
            await using var result = (await session.ExecuteAsync($"SELECT CAST(CAST(@v AS {type}) AS TEXT) FROM t;",
                new Dictionary<string, object?> { ["v"] = text }, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            (await ReadAsync(result)).ShouldHaveSingleItem().ShouldBe(text);
        }
        foreach (string text in invalid)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SELECT CAST(@v AS {type}) FROM t;",
                new Dictionary<string, object?> { ["v"] = text }, cancellationToken: CancellationToken.None));
            error.DiagnosticDetail("CS02022").ShouldContain("CAST from TEXT", Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - CAST: new families compose with filtering, grouping, ordering and typed writes")]
    public async Task Cast_InStatementClauses_ShouldConvertBeforeConsumers()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("cast-composition", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("db");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE source (id INT, d TEXT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO source VALUES (1, '2024-01-02'), (2, '2024-01-01'), (3, '2024-01-01');", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE target (id INT, d DATE, j JSONB, b BYTEA, u UUID, r DOUBLE);", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("INSERT INTO target SELECT id, CAST(d AS DATE), CAST('{\"雪\":1}' AS JSONB), CAST('\\x00ff' AS BYTEA), " +
            "CAST('00112233-4455-6677-8899-aabbccddeeff' AS UUID), CAST('1.25' AS DOUBLE) FROM source;", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO target VALUES (4, CAST('2024-01-03' AS DATE), CAST('true' AS JSONB), CAST('雪' AS BYTEA), " +
            "CAST('00112233-4455-6677-8899-aabbccddeeff' AS UUID), CAST('2.5' AS DOUBLE));", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("UPDATE target SET d = CAST('2024-01-04' AS DATE), j = CAST('null' AS JSONB), b = CAST('\\x' AS BYTEA) WHERE id = 4;", cancellationToken: CancellationToken.None);
        await using var grouped = (await session.ExecuteAsync("SELECT CAST(d AS DATE), COUNT(*) FROM source WHERE CAST(d AS DATE) >= DATE '2024-01-01' " +
            "GROUP BY CAST(d AS DATE) ORDER BY CAST(d AS DATE);", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await using var stored = (await session.ExecuteAsync("SELECT CAST(d AS TEXT), CAST(j AS TEXT), CAST(b AS TEXT), CAST(u AS TEXT), CAST(r AS TEXT) " +
            "FROM target WHERE id = 4;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await RowsAsync(grouped)).ShouldBe(new object?[][] { [new DateOnly(2024, 1, 1), 2L], [new DateOnly(2024, 1, 2), 1L] });
        (await RowsAsync(stored)).ShouldHaveSingleItem().ShouldBe(new object?[]
            { "2024-01-04", "null", "\\x", "00112233-4455-6677-8899-aabbccddeeff", "2.5" });
        var check = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "CREATE TABLE checked_cast (d TEXT CHECK (CAST(d AS DATE) > DATE '2024-01-01'));", cancellationToken: CancellationToken.None));
        check.Message.ShouldBe("CHECK requires deterministic row expressions without parameters, subqueries, or casts.");
    }

    private static async Task<List<object?>> ReadAsync(DatabaseResultSet result)
    {
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(row.GetValue(0)); }
        return values;
    }
    private static async Task<List<object?[]>> RowsAsync(DatabaseResultSet result)
    {
        var values = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return values;
    }
}
