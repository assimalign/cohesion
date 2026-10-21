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

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

[Collection(nameof(KeyValueClientEventSourceCollection))]
public sealed class KeyValueClientTerminalErrorTests
{
    [Theory(DisplayName = "Cohesion Test [KeyValuePair.Client] - Missing database: 200 consecutive typed refusals on InMemory and TCP")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_RepeatedUnknownDatabase_ShouldPreserveTerminalCode(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await KeyValueClientTestHarness.StartAsync(tcp: tcp);
        await using var client = KeyValueClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "absent", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
            ConnectionFactory = harness.Factory,
        });
        var elapsed = Stopwatch.StartNew();
        int refusals = 0;

        // Act: exercise the send/dispose race for at least one second and 200 completions.
        do
        {
            var error = await Should.ThrowAsync<KeyValueClientException>(async () =>
            {
                await using var connection = await client.ConnectAsync(timeout.Token);
            });
            error.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound, $"Refusal {refusals}: {error.Message}");
            error.Kind.ShouldBe(KeyValueClientErrorKind.ConnectionFailure);
            refusals++;
        }
        while ((elapsed.Elapsed < TimeSpan.FromSeconds(1) || refusals < 200) && elapsed.Elapsed < TimeSpan.FromSeconds(45));

        // Assert
        refusals.ShouldBeGreaterThanOrEqualTo(200);
    }
}
