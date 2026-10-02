using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// A client that names a database the server's engine lacks gets the DatabaseNotFound error from
/// the handshake, not a connection the server closes mid-exchange. The Database Studio's wire smoke
/// run observed this case, and no test pinned it.
/// </summary>
public sealed class GraphClientUnknownDatabaseTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Client] - Unknown database: connect fails with DatabaseNotFound")]
    public async Task ConnectAsync_UnknownDatabase_ShouldFailWithDatabaseNotFound()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var client = GraphClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings
            {
                Database = "no_such_database", Principal = "tester", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1,
            },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });

        // Act
        var error = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await using var connection = await client.ConnectAsync(harness.Token);
        });

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound);
        error.Message.ShouldContain("no_such_database", Case.Sensitive);
    }
}
