using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Blob.Client.Tests;

[Collection(nameof(BlobClientEventSourceCollection))]
public sealed class BlobClientTerminalErrorTests
{
    [Theory(DisplayName = "Cohesion Test [Blob.Client] - Missing database: 200 consecutive typed refusals on InMemory and TCP")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_RepeatedUnknownDatabase_ShouldPreserveTerminalCode(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await BlobClientTestHarness.StartAsync(timeout.Token, tcp);
        await using var client = BlobClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "absent", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Factory,
        });
        var elapsed = Stopwatch.StartNew();
        int refusals = 0;

        // Act: exercise the send/dispose race for at least one second and 200 completions.
        do
        {
            var error = await Should.ThrowAsync<BlobClientException>(async () =>
            {
                await using var connection = await client.ConnectAsync(timeout.Token);
            });
            error.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound, $"Refusal {refusals}: {error.Message}");
            refusals++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || refusals < 200) && elapsed.Elapsed < TimeSpan.FromSeconds(45));

        // Assert
        refusals.ShouldBeGreaterThanOrEqualTo(200);
    }
}
