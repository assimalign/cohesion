using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCollectionSliceAdmissionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Slices: semantic admission validates bounds even under EXPLAIN")]
    [InlineData("[0,1,2][..]", "CG00012")]
    [InlineData("NULL[..]", "CG00012")]
    [InlineData("[0,1,2][1.5..]", "CG02014")]
    [InlineData("[0,1,2][..1.5]", "CG02014")]
    [InlineData("[0,1,2][1+0.5..]", "CG02014")]
    [InlineData("[0,1,2][1.0..]", "CG02014")]
    [InlineData("[0,1,2][toFloat(1)..]", "CG02014")]
    [InlineData("[0,1,2]['x'..]", "CG02014")]
    public async Task ExecuteAsync_StaticSliceBound_ShouldRefuseDuringPlanningAsync(string expression, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-slice-admission", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "RETURN ", "EXPLAIN RETURN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + expression, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Slices: dynamic floating bounds retain Neo4j runtime truncation")]
    [InlineData(1.9, 3.9)]
    [InlineData(1.9999999999999998, 3.0)]
    public async Task ExecuteAsync_DynamicSliceBound_ShouldTruncateAtRuntimeAsync(double from, double to)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-dynamic-bound", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["from"] = from, ["to"] = to };
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("RETURN [0,1,2,3][$from..$to]", parameters, CancellationToken.None);
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBeOfType<object?[]>().ShouldBe(new object?[] { 1L, 2L }); count++; }
        count.ShouldBe(1);
    }
}
