using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Sql.Client;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Exercises binary and full-range floating-point predicates through
/// <see cref="SqlDatabaseServer"/> and the production SQL client.
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

    private static ISqlClient CreateClient(ServerTestHarness harness)
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
