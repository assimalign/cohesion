using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1TemporalFieldTests
{
    public static IEnumerable<object[]> ExtractFields()
    {
        string[] fields = ["YEAR", "QUARTER", "MONTH", "WEEK", "DAY", "DOW", "DOY", "HOUR", "MINUTE", "SECOND", "EPOCH"];
        var timestamp = new DateTime(2024, 5, 8, 13, 14, 15).AddTicks(1_234_567);
        decimal[] values = [2024m, 2m, 5m, 19m, 8m, 3m, 129m, 13m, 14m, 15.1234567m, (timestamp.Ticks - DateTime.UnixEpoch.Ticks) / (decimal)TimeSpan.TicksPerSecond];
        for (int index = 0; index < fields.Length; index++)
        {
            yield return [$"EXTRACT({fields[index]} FROM TIMESTAMP '2024-05-08 13:14:15.1234567')", values[index]];
            yield return [$"EXTRACT({fields[index]} FROM TIMESTAMPTZ '2024-05-08 15:14:15.1234567+02:00')", values[index]];
            if (index < 7 || fields[index] == "EPOCH")
            {
                yield return [$"EXTRACT({fields[index]} FROM DATE '2024-05-08')", fields[index] == "EPOCH" ? (timestamp.Date.Ticks - DateTime.UnixEpoch.Ticks) / (decimal)TimeSpan.TicksPerSecond : values[index]];
            }
        }
        yield return ["\"EXTRACT\"('year', TIMESTAMP '2024-05-08 13:14:15')", 2024m];
        yield return ["EXTRACT(DOW FROM DATE '2024-05-12')", 0m];
        yield return ["EXTRACT(WEEK FROM DATE '2021-01-01')", 53m];
        foreach (string type in new[] { "TIME '12:14:15.1234567'", "INTERVAL '36 hours 14 minutes 15.1234567 seconds'" })
        {
            yield return [$"EXTRACT(HOUR FROM {type})", 12m];
            yield return [$"EXTRACT(MINUTE FROM {type})", 14m];
            yield return [$"EXTRACT(SECOND FROM {type})", 15.1234567m];
            yield return [$"EXTRACT(EPOCH FROM {type})", type.StartsWith("TIME", StringComparison.Ordinal) ? 44055.1234567m : 130455.1234567m];
        }
        yield return ["EXTRACT(DAY FROM INTERVAL '36' HOUR)", 1m];
        yield return ["EXTRACT(HOUR FROM INTERVAL '-36' HOUR)", -12m];
        yield return ["EXTRACT(DAY FROM INTERVAL '-36' HOUR)", -1m];
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - EXTRACT: every supported field returns its calendar or normalized duration value")]
    [MemberData(nameof(ExtractFields))]
    public async Task Select_ExtractField_ShouldReturnExpectedValue(string expression, decimal expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("extract-fields", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[expected]]);
    }

    public static IEnumerable<object[]> TruncationFields()
    {
        string[] fields = ["year", "quarter", "month", "week", "day", "hour", "minute", "second", "millisecond", "microsecond"];
        DateTime[] values = [new(2024, 1, 1), new(2024, 4, 1), new(2024, 5, 1), new(2024, 5, 6), new(2024, 5, 8), new(2024, 5, 8, 13, 0, 0), new(2024, 5, 8, 13, 14, 0), new(2024, 5, 8, 13, 14, 15), new DateTime(2024, 5, 8, 13, 14, 15).AddTicks(1_230_000), new DateTime(2024, 5, 8, 13, 14, 15).AddTicks(1_234_560)];
        for (int index = 0; index < fields.Length; index++)
        {
            yield return [$"DATE_TRUNC('{fields[index]}', TIMESTAMP '2024-05-08 13:14:15.1234567')", values[index]];
            yield return [$"DATE_TRUNC('{fields[index]}', TIMESTAMPTZ '2024-05-08 15:14:15.1234567+02:00')", new DateTimeOffset(values[index], TimeSpan.Zero)];
            yield return [$"DATE_TRUNC('{fields[index]}', DATE '2024-05-08')", DateOnly.FromDateTime(values[index])];
        }
        string[] units = ["day", "hour", "minute", "second", "millisecond", "microsecond"];
        long[] ticks = [TimeSpan.TicksPerDay, TimeSpan.TicksPerHour, TimeSpan.TicksPerMinute, TimeSpan.TicksPerSecond, TimeSpan.TicksPerMillisecond, 10];
        var duration = new TimeSpan(1, 12, 14, 15).Add(TimeSpan.FromTicks(1_234_567));
        var time = new TimeOnly(12, 14, 15).Add(TimeSpan.FromTicks(1_234_567));
        for (int index = 0; index < units.Length; index++)
        {
            yield return [$"DATE_TRUNC('{units[index]}', INTERVAL '36 hours 14 minutes 15.1234567 seconds')", new TimeSpan(duration.Ticks / ticks[index] * ticks[index])];
            yield return [$"DATE_TRUNC('{units[index]}', TIME '12:14:15.1234567')", new TimeOnly(time.Ticks / ticks[index] * ticks[index])];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DATE_TRUNC: every supported field retains the input temporal type and value")]
    [MemberData(nameof(TruncationFields))]
    public async Task Select_TruncationField_ShouldReturnExpectedValue(string expression, object expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("truncation-fields", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[expected]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Temporal fields: unknown or incompatible fields fail over an empty table")]
    [InlineData("\"EXTRACT\"('foo', ts)")]
    [InlineData("\"EXTRACT\"('foo', tz)")]
    [InlineData("DATE_TRUNC('foo', ts)")]
    [InlineData("DATE_TRUNC(LOWER('FOO'), ts)")]
    [InlineData("DATE_TRUNC('foo', tz)")]
    [InlineData("DATE_TRUNC('year', tm)")]
    [InlineData("DATE_TRUNC('month', iv)")]
    public async Task Select_InvalidTemporalField_ShouldRefuseDuringPlanning(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-fields", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (ts TIMESTAMP, tz TIMESTAMPTZ, tm TIME, iv INTERVAL)", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("COHSQLE006");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - T1 scalars: NULL in every later strict argument returns NULL")]
    [InlineData("LPAD('x', NULL)")]
    [InlineData("LPAD('x', 5, NULL)")]
    [InlineData("RPAD('x', NULL)")]
    [InlineData("RPAD('x', 5, NULL)")]
    [InlineData("SUBSTRING('x' FROM NULL)")]
    [InlineData("SUBSTRING('x' FROM 1 FOR NULL)")]
    [InlineData("POWER(2, NULL)")]
    [InlineData("ROUND(2, NULL)")]
    [InlineData("MOD(2, NULL)")]
    [InlineData("REPLACE('x', NULL, 'y')")]
    [InlineData("REPLACE('x', 'x', NULL)")]
    [InlineData("STRPOS('x', NULL)")]
    [InlineData("POSITION(NULL IN 'x')")]
    [InlineData("POSITION('x' IN NULL)")]
    [InlineData("TRIM(BOTH NULL FROM 'x')")]
    [InlineData("LTRIM('x', NULL)")]
    [InlineData("RTRIM('x', NULL)")]
    [InlineData("LEFT('x', NULL)")]
    [InlineData("RIGHT('x', NULL)")]
    [InlineData("DATE_TRUNC(NULL, DATE '2024-01-01')")]
    [InlineData("DATE_TRUNC('day', NULL)")]
    public async Task Select_NullLaterArgument_ShouldReturnNull(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-arguments", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1)", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[null]]);
    }
}
