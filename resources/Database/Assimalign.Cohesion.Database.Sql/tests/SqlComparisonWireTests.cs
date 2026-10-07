using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Exercises binary, full-range floating-point and temporal-identity predicates
/// through <see cref="SqlDatabaseServer"/> and the production SQL client.
/// </summary>
public sealed class SqlComparisonWireTests
{
    /// <summary>Unsigned binary ordering examines the late differing byte over the wire.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Comparison: binary WHERE uses late unsigned bytes through the client")]
    public async Task BinaryWhere_ShouldCompareLateUnsignedByteOverTheWire()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = CreateClient(harness);
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync("CREATE TABLE binary_values (id INT, payload BINARY)",
            cancellationToken: TestTimeout.Token());

        byte[] lower = new byte[64];
        byte[] higher = new byte[64];
        lower[63] = 0x7f;
        higher[63] = 0x80;
        await connection.ExecuteAsync(
            "INSERT INTO binary_values VALUES (1, @lower), (2, @higher), (3, @higher)",
            new Dictionary<string, object?> { ["lower"] = lower, ["higher"] = higher },
            TestTimeout.Token());

        var result = await connection.QueryAsync(
            "SELECT id FROM binary_values WHERE payload > @lower ORDER BY id",
            new Dictionary<string, object?> { ["lower"] = lower }, TestTimeout.Token());

        result.Select(row => row["id"]).ShouldBe(new object?[] { 2, 3 });
    }

    /// <summary>A finite double beyond decimal's range survives parameter binding and WHERE.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Comparison: full-range double WHERE works through the client")]
    public async Task LargeDoubleWhere_ShouldCompareOutsideDecimalRangeOverTheWire()
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = CreateClient(harness);
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync("CREATE TABLE double_values (id INT, amount DOUBLE)",
            cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync(
            "INSERT INTO double_values VALUES (1, @lower), (2, @higher)",
            new Dictionary<string, object?> { ["lower"] = 1e99, ["higher"] = 1e100 },
            TestTimeout.Token());

        var result = await connection.QueryAsync(
            "SELECT amount FROM double_values WHERE amount > @lower",
            new Dictionary<string, object?> { ["lower"] = 1e99 }, TestTimeout.Token());

        result.Select(row => row["amount"]).ShouldBe(new object?[] { 1e100 });
    }

    /// <summary>
    /// TIMESTAMP parameters of every <see cref="DateTimeKind"/> cross the wire with
    /// their kind intact and seek an index to the same rows a scan returns (#1099).
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Comparison: TIMESTAMP index seeks match scans across DateTime kinds through the client")]
    public async Task TimestampSeek_ShouldMatchScanAcrossKindsOverTheWire()
    {
        var noon = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Unspecified);
        var rows = new Dictionary<string, object?>
        {
            ["a"] = noon,
            ["b"] = DateTime.SpecifyKind(noon, DateTimeKind.Utc),
            ["c"] = DateTime.SpecifyKind(noon, DateTimeKind.Local),
            ["d"] = DateTime.SpecifyKind(noon.AddHours(-1), DateTimeKind.Utc),
            ["e"] = DateTime.SpecifyKind(noon.AddHours(1), DateTimeKind.Local),
        };
        var probes = new object[]
        {
            noon, DateTime.SpecifyKind(noon, DateTimeKind.Utc), DateTime.SpecifyKind(noon, DateTimeKind.Local),
        };

        await AssertTemporalSeekParityOverTheWireAsync("TIMESTAMP", rows, probes, upper: noon.AddHours(1));
    }

    /// <summary>
    /// TIMESTAMPTZ parameters at different offsets cross the wire and seek an
    /// index to every row holding the same instant, as a scan does (#1099).
    /// </summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Comparison: TIMESTAMPTZ index seeks match scans across offsets through the client")]
    public async Task TimestampWithTimeZoneSeek_ShouldMatchScanAcrossOffsetsOverTheWire()
    {
        var instant = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        var rows = new Dictionary<string, object?>
        {
            ["a"] = instant,
            ["b"] = instant.ToOffset(TimeSpan.FromHours(3)),
            ["c"] = instant.ToOffset(TimeSpan.FromHours(-5.5)),
            ["d"] = instant.AddHours(-1).ToOffset(TimeSpan.FromHours(1)),
            ["e"] = instant.AddHours(1).ToOffset(TimeSpan.FromHours(-8)),
        };
        var probes = new object[]
        {
            instant, instant.ToOffset(TimeSpan.FromHours(14)), instant.ToOffset(TimeSpan.FromHours(-12)),
        };

        await AssertTemporalSeekParityOverTheWireAsync("TIMESTAMPTZ", rows, probes, upper: instant.AddHours(1).ToOffset(TimeSpan.FromHours(2)));
    }

    private static async Task AssertTemporalSeekParityOverTheWireAsync(
        string type, Dictionary<string, object?> rows, object[] probes, object upper)
    {
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = CreateClient(harness);
        await using var connection = await client.ConnectAsync(TestTimeout.Token());
        await connection.ExecuteAsync($"CREATE TABLE scanned (id INT, ts {type})", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync($"CREATE TABLE indexed (id INT, ts {type} UNIQUE)", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync($"CREATE TABLE duplicated (id INT, ts {type})", cancellationToken: TestTimeout.Token());
        await connection.ExecuteAsync("CREATE INDEX ix_ts ON duplicated (ts)", cancellationToken: TestTimeout.Token());
        foreach (string table in new[] { "scanned", "duplicated" })
        {
            await connection.ExecuteAsync($"INSERT INTO {table} VALUES (1, @a), (2, @b), (3, @c), (4, @d), (5, @e)", rows, TestTimeout.Token());
        }

        // The UNIQUE column keeps one member of the equal-value class (a), and
        // rejects the others over the wire.
        await connection.ExecuteAsync("INSERT INTO indexed VALUES (1, @a), (4, @d), (5, @e)", rows, TestTimeout.Token());
        foreach (string equal in new[] { "b", "c" })
        {
            await Should.ThrowAsync<SqlClientException>(async () => await connection.ExecuteAsync(
                "INSERT INTO indexed VALUES (9, @v)", new Dictionary<string, object?> { ["v"] = rows[equal] }, TestTimeout.Token()));
        }

        string[] predicates = ["ts = @p", "ts >= @p", "ts > @p", "ts <= @p", "ts < @p", "ts BETWEEN @p AND @q"];

        foreach (object probe in probes)
        {
            var parameters = new Dictionary<string, object?> { ["p"] = probe, ["q"] = upper };
            foreach (string predicate in predicates)
            {
                var expected = await IdsAsync(connection, $"SELECT id FROM scanned WHERE {predicate} ORDER BY id", parameters);
                ServerAccessPath(harness).ShouldBe("scan");
                foreach (string table in new[] { "duplicated", "indexed" })
                {
                    string sql = $"SELECT id FROM {table} WHERE {predicate} ORDER BY id";
                    var actual = await IdsAsync(connection, sql, parameters);

                    // The server itself sought the index, with the parameters it
                    // decoded off the wire.
                    ServerAccessPath(harness).ShouldStartWith("seek:", customMessage: $"{table}: {predicate} with {probe}");
                    actual.ShouldBe(table == "indexed" ? expected.Where(id => id is 1 or 4 or 5).ToArray() : expected,
                        $"{table}: {predicate} with {probe}");
                }
            }

            (await IdsAsync(connection, "SELECT id FROM duplicated WHERE ts = @p ORDER BY id", parameters)).ShouldBe(new object?[] { 1, 2, 3 });
        }
    }

    private static async Task<object?[]> IdsAsync(SqlConnection connection, string sql, Dictionary<string, object?> parameters)
        => (await connection.QueryAsync(sql, parameters, TestTimeout.Token())).Select(row => row["id"]).ToArray();

    /// <summary>
    /// The access path of the last statement the server executed for the test's
    /// single client connection: the server session's own metrics, not a plan
    /// rebuilt in the test process.
    /// </summary>
    private static string ServerAccessPath(ServerTestHarness harness)
    {
        var session = harness.Server.GetSessionsSnapshot().ShouldHaveSingleItem().ShouldBeOfType<SqlDatabaseServerSession>();
        var databaseSession = session.DatabaseSession.ShouldBeOfType<SqlDatabaseSession>();
        return databaseSession.LastStatementMetrics.ShouldNotBeNull().AccessPath;
    }

    private static SqlClient CreateClient(ServerTestHarness harness)
        => SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = ServerTestHarness.DatabaseName,
                Principal = "comparison-tests",
                EndPoint = harness.Listener.EndPoint,
            },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
}
