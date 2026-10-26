using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphBeginAliasTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - BEGIN aliases START TRANSACTION for commit and rollback (#1725)")]
    [InlineData("COMMIT", 1L)]
    [InlineData("ROLLBACK", 0L)]
    public async Task ExecuteAsync_BeginAlias_ShouldOwnSessionTransaction(string end, long expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("begin", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        session.CurrentTransaction.ShouldNotBeNull();
        await session.ExecuteAsync("INSERT (:Pending)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync(end, cancellationToken: CancellationToken.None);

        // Assert
        session.CurrentTransaction.ShouldBeNull();
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n) RETURN count(*)", cancellationToken: CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBe(expected); count++; }
        count.ShouldBe(1);
    }
}
