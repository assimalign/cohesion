using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphCollectionFilterAdmissionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Comprehensions: known non-Boolean filters fail independently of iteration")]
    [InlineData("[x IN [] WHERE 1]")]
    [InlineData("[x IN NULL WHERE 1]")]
    [InlineData("[x IN [] WHERE 'x']")]
    [InlineData("[x IN NULL WHERE []]")]
    [InlineData("[x IN [] WHERE size([])]")]
    [InlineData("[x IN [] WHERE 1*2]")]
    public async Task ExecuteAsync_StaticFilter_ShouldRefuseDuringPlanningAsync(string expression)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-filter-admission", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "RETURN ", "EXPLAIN RETURN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + expression, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Comprehensions: unknown filter types retain iteration-time checks")]
    public async Task ExecuteAsync_UnknownFilter_ShouldValidateOnlyVisitedElementsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("collection-unknown-filter", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:N {k:1})", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string source in new[] { "[]", "NULL" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n:N) RETURN [x IN " + source + " WHERE n.k]", cancellationToken: CancellationToken.None);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            {
                if (source == "NULL") { row.GetValue(0).ShouldBeNull(); }
                else { row.GetValue(0).ShouldBeOfType<object?[]>().ShouldBeEmpty(); }
                count++;
            }
            count.ShouldBe(1);
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (n:N) RETURN [x IN [1] WHERE n.k]", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
    }
}
