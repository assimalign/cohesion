using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1ScalarTests
{
    internal static readonly DateTimeOffset Instant = new(2024, 3, 4, 12, 34, 56, TimeSpan.Zero);

    public static IEnumerable<object?[]> Cases() =>
    [
        ["COALESCE", "COALESCE(s, 'fallback')", "A😀b", "fallback", DatabaseType.String],
        ["NULLIF", "NULLIF(s, s)", null, null, DatabaseType.String],
        ["UPPER", "UPPER(s)", "A😀B", null, DatabaseType.String],
        ["LOWER", "LOWER(s)", "a😀b", null, DatabaseType.String],
        ["LENGTH", "LENGTH(s)", 3L, null, DatabaseType.Int64],
        ["CHAR_LENGTH", "CHAR_LENGTH(s)", 3L, null, DatabaseType.Int64],
        ["CHARACTER_LENGTH", "CHARACTER_LENGTH(s)", 3L, null, DatabaseType.Int64],
        ["TRIM", "TRIM(s)", "A😀b", null, DatabaseType.String],
        ["LTRIM", "LTRIM(s, 'A')", "😀b", null, DatabaseType.String],
        ["RTRIM", "RTRIM(s, 'b')", "A😀", null, DatabaseType.String],
        ["SUBSTRING", "SUBSTRING(s FROM 2 FOR 1)", "😀", null, DatabaseType.String],
        ["REPLACE", "REPLACE(s, '😀', 'x')", "Axb", null, DatabaseType.String],
        ["CONCAT", "CONCAT(s, NULL, '!')", "A😀b!", "!", DatabaseType.String],
        ["CONCAT_WS", "CONCAT_WS('-', s, NULL, '!')", "A😀b-!", "!", DatabaseType.String],
        ["POSITION", "POSITION('b' IN s)", 3L, null, DatabaseType.Int64],
        ["STRPOS", "STRPOS(s, '😀')", 2L, null, DatabaseType.Int64],
        ["LEFT", "LEFT(s, 2)", "A😀", null, DatabaseType.String],
        ["RIGHT", "RIGHT(s, 2)", "😀b", null, DatabaseType.String],
        ["LPAD", "LPAD(s, 5, '😀')", "😀😀A😀b", null, DatabaseType.String],
        ["RPAD", "RPAD(s, 5, '😀')", "A😀b😀😀", null, DatabaseType.String],
        ["REVERSE", "REVERSE(s)", "b😀A", null, DatabaseType.String],
        ["ABS", "ABS(i)", 2L, null, DatabaseType.Int64],
        ["ROUND", "ROUND(n)", 3m, null, DatabaseType.Decimal],
        ["FLOOR", "FLOOR(n)", 2m, null, DatabaseType.Decimal],
        ["CEILING", "CEILING(n)", 3m, null, DatabaseType.Decimal],
        ["CEIL", "CEIL(n)", 3m, null, DatabaseType.Decimal],
        ["POWER", "POWER(n, 2)", 6.25m, null, DatabaseType.Decimal],
        ["SQRT", "SQRT(ABS(i))", Math.Sqrt(2), null, DatabaseType.Float64],
        ["MOD", "MOD(i, 2)", 0L, null, DatabaseType.Int64],
        ["SIGN", "SIGN(i)", -1L, null, DatabaseType.Int64],
        ["EXP", "EXP(i)", Math.Exp(-2), null, DatabaseType.Float64],
        ["LN", "LN(ABS(i))", Math.Log(2), null, DatabaseType.Float64],
        ["LOG10", "LOG10(ABS(i))", Math.Log10(2), null, DatabaseType.Float64],
        ["CURRENT_DATE", "CURRENT_DATE", new DateOnly(2024, 3, 4), new DateOnly(2024, 3, 4), DatabaseType.Date],
        ["CURRENT_TIME", "CURRENT_TIME", new TimeOnly(12, 34, 56), new TimeOnly(12, 34, 56), DatabaseType.Time],
        ["CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP", Instant, Instant, DatabaseType.DateTimeOffset],
        ["LOCALTIME", "LOCALTIME", new TimeOnly(12, 34, 56), new TimeOnly(12, 34, 56), DatabaseType.Time],
        ["LOCALTIMESTAMP", "LOCALTIMESTAMP", Instant.UtcDateTime, Instant.UtcDateTime, DatabaseType.DateTime],
        ["NOW", "NOW()", Instant, Instant, DatabaseType.DateTimeOffset],
        ["EXTRACT", "EXTRACT(YEAR FROM d)", 2024m, null, DatabaseType.Decimal],
        ["DATE_TRUNC", "DATE_TRUNC('month', d)", new DateOnly(2024, 3, 1), null, DatabaseType.Date],
        ["GEN_RANDOM_UUID", "GEN_RANDOM_UUID()", typeof(Guid), typeof(Guid), DatabaseType.Guid],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: value, NULL and empty-input metadata agree")]
    [MemberData(nameof(Cases))]
    public async Task Select_EveryScalar_ShouldReturnValueNullAndStaticType(string name, string expression, object? expected, object? nullExpected, DatabaseType type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("t1-scalars", new() { TimeProvider = new ScalarClock(Instant) });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT, s TEXT, i BIGINT, n DECIMAL, d DATE);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1, 'A😀b', -2, 2.5, DATE '2024-03-04'), (2, NULL, NULL, NULL, NULL);", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t ORDER BY id;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);
        await using var empty = (await session.ExecuteAsync($"SELECT {expression} FROM t WHERE id = 0;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns[0].Type.ShouldBe(type, name);
        empty.Columns[0].Type.ShouldBe(type, name);
        (await ReadAsync(empty)).ShouldBeEmpty();
        rows.Count.ShouldBe(2);
        VerifyValue(rows[0][0], expected);
        VerifyValue(rows[1][0], nullExpected);
        if (type == DatabaseType.Guid) { rows[0][0].ShouldNotBe(rows[1][0]); }
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: invalid calls fail planning on empty inputs")]
    [InlineData("UPPER(1)")]
    [InlineData("LOWER(1)")]
    [InlineData("LENGTH(1)")]
    [InlineData("ABS('x')")]
    [InlineData("NULLIF(i)")]
    [InlineData("LPAD(s)")]
    [InlineData("GEN_RANDOM_UUID(1)")]
    [InlineData("NOW(1)")]
    [InlineData("SUBSTRING(s FROM 'x')")]
    [InlineData("EXTRACT(HOUR FROM d)")]
    [InlineData("ROUND(f, 2)")]
    [InlineData("DATE_TRUNC('day', i)")]
    public async Task Select_InvalidSignature_ShouldFailBeforeReadingRows(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("t1-signatures", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (i INT, s TEXT, d DATE, f DOUBLE PRECISION);", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t;", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("COHSQLE006");
        failure.Message.ShouldContain(expression.Split('(')[0], Case.Sensitive);
        failure.Message.ShouldContain("Accepted", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - T1 scalars: numeric domain faults retain coded evaluation failures")]
    [InlineData("SQRT(-1)", "COHSQLE002")]
    [InlineData("LN(0)", "COHSQLE002")]
    [InlineData("LOG10(-1)", "COHSQLE002")]
    [InlineData("POWER(0, -1)", "COHSQLE002")]
    [InlineData("POWER(10, 400)", "COHSQLE002")]
    [InlineData("POWER(0.1, -29)", "COHSQLE002")]
    [InlineData("EXP(1000)", "COHSQLE002")]
    [InlineData("MOD(1, 0)", "COHSQLE001")]
    [InlineData("SUBSTRING('x' FROM 1 FOR -1)", "COHSQLE002")]
    public async Task Select_DomainFault_ShouldReturnCodedDatabaseException(string expression, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("t1-domains", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t;", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe(code);
    }

    internal static void VerifyValue(object? actual, object? expected)
    {
        if (expected is Type)
        {
            var uuid = actual.ShouldBeOfType<Guid>();
            (uuid.ToByteArray()[7] >> 4).ShouldBe(4);
        }
        else { actual.ShouldBe(expected); }
    }

    internal static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
