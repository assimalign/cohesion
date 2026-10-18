using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlT1TemporalTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Clock: API and SQL transactions retain BEGIN time across statements and restart after end")]
    public async Task Execute_TransactionClock_ShouldCaptureExactlyAtBegin()
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("transaction-clock", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1), (2);", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (bool sqlBegin in new[] { false, true })
        {
            foreach (bool commit in new[] { false, true })
            {
                var begin = clock.Instant;
                if (sqlBegin) { await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None); }
                else { await session.BeginTransactionAsync(CancellationToken.None); }
                clock.Instant += TimeSpan.FromDays(1);
                (await Rows(session, "SELECT CURRENT_TIMESTAMP, NOW() FROM t;")).ShouldAllBe(row => Equals(row[0], begin) && Equals(row[1], begin));
                clock.Instant += TimeSpan.FromDays(1);
                (await Rows(session, "SELECT NOW() FROM t;")).ShouldAllBe(row => Equals(row[0], begin));
                await session.ExecuteAsync(commit ? "COMMIT" : "ROLLBACK", cancellationToken: CancellationToken.None);
                (await Rows(session, "SELECT NOW() FROM t;")).ShouldAllBe(row => Equals(row[0], clock.Instant));
                clock.Instant += TimeSpan.FromMinutes(1);
                (await Rows(session, "SELECT CURRENT_TIMESTAMP FROM t;")).ShouldAllBe(row => Equals(row[0], clock.Instant));
            }
        }
        await session.ExecuteAsync("CREATE FUNCTION clock_body() RETURNS TIMESTAMPTZ STABLE RETURN NOW();", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        var nestedBegin = clock.Instant;
        clock.Instant += TimeSpan.FromDays(1);
        (await Rows(session, "SELECT clock_body(), (SELECT NOW() FROM t child WHERE child.id = parent.id) FROM t parent;")).ShouldAllBe(row => Equals(row[0], nestedBegin) && Equals(row[1], nestedBegin));
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);

        // Interleaved transactions replace the environment cache without changing retained clocks.
        await using var other = await database.CreateSessionAsync(CancellationToken.None);
        await session.BeginTransactionAsync(CancellationToken.None);
        var firstInstant = clock.Instant;
        clock.Instant += TimeSpan.FromDays(1);
        await other.BeginTransactionAsync(CancellationToken.None);
        var secondInstant = clock.Instant;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            (await Rows(session, "SELECT NOW(), clock_body() FROM t;")).ShouldAllBe(row => Equals(row[0], firstInstant) && Equals(row[1], firstInstant));
            (await Rows(other, "SELECT NOW(), clock_body() FROM t;")).ShouldAllBe(row => Equals(row[0], secondInstant) && Equals(row[1], secondInstant));
        }
        await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
        await other.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Clock: temporal seek bounds and every projected row use the transaction instant")]
    [InlineData("TIMESTAMP", "LOCALTIMESTAMP")]
    [InlineData("TIMESTAMPTZ", "NOW()")]
    public async Task Select_ClockBound_ShouldMatchScanAndProjection(string type, string current)
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var engine = SqlDatabaseEngine.Create("clock-seek", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync($"CREATE TABLE t (id INT, ts {type});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO t VALUES (1, {current} - INTERVAL '2' DAY), (2, {current} - INTERVAL '12' HOUR), (3, {current});", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix_clock ON t(ts);", cancellationToken: CancellationToken.None);
        await session.BeginTransactionAsync(CancellationToken.None);
        clock.Instant += TimeSpan.FromDays(3);
        string query = $"SELECT id, CURRENT_TIMESTAMP FROM t WHERE ts > {current} - INTERVAL '1' DAY ORDER BY id;";

        // Act
        var seek = await Rows(session, query);
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldStartWith("seek:");
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
        clock.Instant = SqlT1ScalarTests.Instant;
        await session.ExecuteAsync("DROP INDEX ix_clock ON t;", cancellationToken: CancellationToken.None);
        var scan = await Rows(session, query);

        // Assert
        session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
        seek.ShouldBe(scan);
        seek.Select(row => row[0]).ShouldBe(new object?[] { 2, 3 });
        seek.ShouldAllBe(row => Equals(row[1], SqlT1ScalarTests.Instant));
    }

    public static IEnumerable<object[]> Arithmetic() =>
    [
        ["DATE '2024-03-04' + 2", new DateOnly(2024, 3, 6), DatabaseType.Date],
        ["DATE '2024-03-04' - 2", new DateOnly(2024, 3, 2), DatabaseType.Date],
        ["DATE '2024-03-04' - DATE '2024-03-01'", 3, DatabaseType.Int32],
        ["DATE '2024-03-04' + INTERVAL '1' HOUR", new DateTime(2024, 3, 4, 1, 0, 0), DatabaseType.DateTime],
        ["DATE '2024-03-04' - INTERVAL '1' HOUR", new DateTime(2024, 3, 3, 23, 0, 0), DatabaseType.DateTime],
        ["TIMESTAMP '2024-03-04 01:00:00' + INTERVAL '1' HOUR", new DateTime(2024, 3, 4, 2, 0, 0), DatabaseType.DateTime],
        ["TIMESTAMPTZ '2024-03-04 01:00:00+02:00' - INTERVAL '1' HOUR", new DateTimeOffset(2024, 3, 4, 0, 0, 0, TimeSpan.FromHours(2)), DatabaseType.DateTimeOffset],
        ["TIMESTAMP '2024-03-04 01:00:00' - TIMESTAMPTZ '2024-03-04 01:00:00+02:00'", TimeSpan.FromHours(2), DatabaseType.TimeSpan],
        ["INTERVAL '1' DAY + INTERVAL '2' HOUR", TimeSpan.FromHours(26), DatabaseType.TimeSpan],
        ["INTERVAL '1' DAY - INTERVAL '2' HOUR", TimeSpan.FromHours(22), DatabaseType.TimeSpan],
        ["-INTERVAL '1' DAY", TimeSpan.FromDays(-1), DatabaseType.TimeSpan],
    ];

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temporal arithmetic: operators return documented values and types")]
    [MemberData(nameof(Arithmetic))]
    public async Task Select_TemporalArithmetic_ShouldReturnTypedValue(string expression, object expected, DatabaseType type)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("temporal-arithmetic", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (1);", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync($"SELECT {expression} FROM t;", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns[0].Type.ShouldBe(type);
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldHaveSingleItem()[0].ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - Temporal arithmetic: invalid pairs fail planning even over an empty table")]
    [InlineData("DATE '2024-01-01' + DATE '2024-01-02'")]
    [InlineData("TIME '12:00' + INTERVAL '1' HOUR")]
    [InlineData("DATE '2024-01-01' * 2")]
    [InlineData("INTERVAL '1' DAY + 2")]
    [InlineData("TIMESTAMP '2024-01-01' - DATE '2024-01-01'")]
    public async Task Select_InvalidTemporalPair_ShouldRejectBeforeRows(string expression)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("invalid-temporal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (id INT);", cancellationToken: CancellationToken.None);

        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t;", cancellationToken: CancellationToken.None).AsTask());
        failure.Diagnostics[0].Code.ShouldBe("COHSQLE003");
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal operands: interval columns and parameters share literal arithmetic and coded range faults")]
    public async Task Select_IntervalColumnsAndParameters_ShouldUseTheSameArithmetic()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("interval-operands", new() { TimeProvider = new ScalarClock(SqlT1ScalarTests.Instant) });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (ts TIMESTAMP, duration INTERVAL);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (TIMESTAMP '2024-03-04 12:00:00', INTERVAL '1' HOUR);", cancellationToken: CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["duration"] = TimeSpan.FromHours(2) };

        // Act
        await using var result = (await session.ExecuteAsync("SELECT ts + duration, ts - @duration, -duration FROM t WHERE ts > NOW() - INTERVAL '1' DAY;",
            parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.DateTime, DatabaseType.DateTime, DatabaseType.TimeSpan]);
        (await SqlT1ScalarTests.ReadAsync(result)).ShouldBe([[new DateTime(2024, 3, 4, 13, 0, 0), new DateTime(2024, 3, 4, 10, 0, 0), TimeSpan.FromHours(-1)]]);
        foreach (string expression in new[] { "DATE '9999-12-31' + 1", "TIMESTAMP '9999-12-31 23:59:59' + INTERVAL '1' DAY", "-@duration" })
        {
            parameters["duration"] = TimeSpan.MinValue;
            var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync($"SELECT {expression} FROM t;", parameters, CancellationToken.None).AsTask());
            failure.Diagnostics[0].Code.ShouldBe("COHSQLE002");
        }
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Temporal identity: DATE midnight equals TIMESTAMP and TIMESTAMPTZ with equal grouping hashes")]
    public async Task Compare_CrossTemporalPairs_ShouldHaveEqualHashesAndGrouping()
    {
        // Arrange
        object[] values = [new DateOnly(2024, 3, 4), new DateTime(2024, 3, 4), new DateTimeOffset(2024, 3, 4, 2, 0, 0, TimeSpan.FromHours(2))];

        // Act / Assert
        foreach (object left in values)
        {
            foreach (object right in values)
            {
                SqlValueComparer.Compare(left, right).ShouldBe(0);
                SqlValueComparer.GetHashCode(left, Collation.Binary).ShouldBe(SqlValueComparer.GetHashCode(right, Collation.Binary));
            }
        }
        await using var engine = SqlDatabaseEngine.Create("temporal-grouping", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t (d DATE, ts TIMESTAMP);", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES (DATE '2024-03-04', NULL), (NULL, TIMESTAMP '2024-03-04');", cancellationToken: CancellationToken.None);
        (await Rows(session, "SELECT DISTINCT COALESCE(d, ts) FROM t;")).Count.ShouldBe(1);
        (await Rows(session, "SELECT COALESCE(d, ts), COUNT(*) FROM t GROUP BY COALESCE(d, ts);")).ShouldHaveSingleItem()[1].ShouldBe(2L);
        (await Rows(session, "SELECT d = ts FROM t WHERE d IS NOT NULL;")).ShouldHaveSingleItem()[0].ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Wire clock: the internal fake clock fixes every statement in a transaction")]
    public async Task Wire_FakeClock_ShouldRetainTransactionStart()
    {
        // Arrange
        var clock = new ScalarClock(SqlT1ScalarTests.Instant);
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options => options.TimeProvider = clock);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("BEGIN").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        clock.Instant += TimeSpan.FromDays(1);

        // Act / Assert
        for (int index = 0; index < 2; index++)
        {
            await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT NOW() FROM users WHERE id = 1").Encode());
            await client.ExpectAsync(ProtocolMessageType.ResultHeader);
            var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
            DatabaseValueCodec.DecodeComponent(row.Payload.Span).ShouldBe(SqlT1ScalarTests.Instant);
            await client.ExpectAsync(ProtocolMessageType.ResultComplete);
            clock.Instant += TimeSpan.FromDays(1);
        }
    }

    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await SqlT1ScalarTests.ReadAsync(result);
    }
}
