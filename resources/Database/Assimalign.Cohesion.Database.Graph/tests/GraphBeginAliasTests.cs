using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphBeginAliasTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - BEGIN aliases START TRANSACTION for commit and rollback (#1725)")]
    [InlineData("BEGIN", "COMMIT", 1L)]
    [InlineData("BEGIN", "ROLLBACK", 0L)]
    [InlineData("BEGIN TRANSACTION", "COMMIT", 1L)]
    [InlineData("BEGIN TRANSACTION", "ROLLBACK", 0L)]
    public async Task ExecuteAsync_BeginAlias_ShouldOwnSessionTransaction(string begin, string end, long expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("begin", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await session.ExecuteAsync(begin, cancellationToken: CancellationToken.None);
        session.CurrentTransaction.ShouldNotBeNull().IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
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

    [Theory(DisplayName = "Cohesion Test [Graph] - BEGIN options retain CG00002 without starting a transaction (#1725)")]
    [InlineData("BEGIN")]
    [InlineData("BEGIN TRANSACTION")]
    public async Task ExecuteAsync_BeginOptions_ShouldRefuseBeforeStarting(string begin)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("begin", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        foreach (string option in new[] { "WORK", "TRAN", "READ ONLY", "READ WRITE", "ISOLATION LEVEL SNAPSHOT",
            "ISOLATION LEVEL READ COMMITTED", "ISOLATION LEVEL SERIALIZABLE", "DEFERRABLE", "NOT DEFERRABLE" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(begin + " " + option, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG00002");
            session.CurrentTransaction.ShouldBeNull();
        }
    }
}
