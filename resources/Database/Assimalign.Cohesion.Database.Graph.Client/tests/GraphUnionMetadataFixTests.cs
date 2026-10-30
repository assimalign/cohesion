using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphUnionMetadataFixTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - UNION: metadata covers later numeric operands on both transports")]
    [InlineData(false, "RETURN 1 AS x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(true, "RETURN 1 AS x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(false, "WITH NULL AS x RETURN x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(true, "WITH NULL AS x RETURN x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(false, "RETURN NULL AS x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(true, "RETURN NULL AS x UNION RETURN 1.5 AS x", DatabaseType.Float64)]
    [InlineData(false, "RETURN NULL AS x UNION RETURN count(*) AS x", DatabaseType.Int64)]
    [InlineData(true, "RETURN NULL AS x UNION RETURN count(*) AS x", DatabaseType.Int64)]
    public async Task QueryAsync_LaterOperandTypes_ShouldReconcileMetadataAsync(bool tcp, string query, DatabaseType expected)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var result = await connection.QueryAsync(query, cancellationToken: harness.Token);
        // Assert
        result.Columns.ShouldHaveSingleItem().Name.ShouldBe("x");
        result.Columns[0].Type.ShouldBe(expected);
        result.Count.ShouldBe(2);
        if (expected == DatabaseType.Float64)
        { result.Select(row => row[0]).ShouldBe(new object?[] { query.Contains("NULL") ? null : 1L, 1.5d }); }
        else { result.Select(row => row[0]).ShouldBe(new object?[] { null, 1L }); }
    }
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - UNION: incompatible numeric metadata uses the existing unknown type")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_DecimalAndDouble_ShouldAdvertiseUnknownAsync(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var result = await connection.QueryAsync("RETURN CAST(1 AS DECIMAL) AS x UNION RETURN CAST(1 AS FLOAT64) AS x", cancellationToken: harness.Token);
        // Assert
        result.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Null);
        result.ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>();
    }
}
