using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Connections.InMemory.Tests;

public class InMemoryConnectionPairTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Should round-trip a single payload both directions")]
    public async Task Create_ClientServerEcho_ShouldRoundTripPayload()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        byte[] payload = [1, 2, 3, 4, 5];

        // Act
        await client.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await server.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        await server.Output.WriteAsync(received, cancellation.Token);
        byte[] echoed = await client.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        echoed.ShouldBe(payload);

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Should support multiple live round-trips (not single-shot)")]
    public async Task Create_MultipleRoundTrips_ShouldExchangeEachCycle()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Act & Assert — three independent request/response cycles over the same live connection,
        // which the previous single-shot doubles could not do.
        for (int i = 1; i <= 3; i++)
        {
            byte[] request = [(byte)i];
            await client.Output.WriteAsync(request, cancellation.Token);

            byte[] serverGot = await server.Input.ReadExactlyAsync(1, cancellation.Token);
            serverGot.ShouldBe(request);

            byte[] response = [(byte)(i + 100)];
            await server.Output.WriteAsync(response, cancellation.Token);

            byte[] clientGot = await client.Input.ReadExactlyAsync(1, cancellation.Token);
            clientGot.ShouldBe(response);
        }

        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Completing client output should complete server read")]
    public async Task Complete_OnClientOutput_ShouldCompleteServerRead()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        byte[] payload = [10, 20, 30];
        await client.Output.WriteAsync(payload, cancellation.Token);
        byte[] received = await server.Input.ReadExactlyAsync(payload.Length, cancellation.Token);

        // Act — graceful half-close.
        await client.Output.CompleteAsync();
        ReadResult result = await server.Input.ReadAsync(cancellation.Token);

        // Assert
        received.ShouldBe(payload);
        result.IsCompleted.ShouldBeTrue();
        result.Buffer.IsEmpty.ShouldBeTrue();

        server.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Completing output should transition state to Closing")]
    public async Task Complete_OnOutput_ShouldTransitionToClosing()
    {
        // Arrange
        (Connection client, Connection _) = InMemoryConnectionPair.Create();
        client.State.ShouldBe(ConnectionState.Open);

        // Act
        await client.Output.CompleteAsync();

        // Assert
        client.State.ShouldBe(ConnectionState.Closing);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Abort should surface the reason on the peer read")]
    public async Task Abort_OnClient_ShouldSurfaceReasonOnServerRead()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        InvalidOperationException reason = new("boom");

        // Act
        client.Abort(reason);

        // Assert
        client.State.ShouldBe(ConnectionState.Aborted);
        client.ConnectionClosed.IsCancellationRequested.ShouldBeTrue();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await server.Input.ReadAsync(cancellation.Token));
        thrown.ShouldBeSameAs(reason);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Abort without a reason should surface a ConnectionAbortedException on the peer")]
    public async Task Abort_WithoutReason_ShouldSurfaceConnectionAbortedOnPeer()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Act
        client.Abort();

        // Assert
        await Should.ThrowAsync<ConnectionAbortedException>(
            async () => await server.Input.ReadAsync(cancellation.Token));
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Disposing one end should complete the peer read")]
    public async Task DisposeAsync_OnClient_ShouldCompletePeerRead()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TestTimeout);
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Act
        await client.DisposeAsync();
        ReadResult result = await server.Input.ReadAsync(cancellation.Token);

        // Assert
        client.State.ShouldBe(ConnectionState.Closed);
        result.IsCompleted.ShouldBeTrue();
        server.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: DisposeAsync should be idempotent")]
    public async Task DisposeAsync_CalledTwice_ShouldBeIdempotent()
    {
        // Arrange
        (Connection client, Connection _) = InMemoryConnectionPair.Create();

        // Act
        await client.DisposeAsync();
        Exception? exception = await Record.ExceptionAsync(async () => await client.DisposeAsync());

        // Assert
        exception.ShouldBeNull();
        client.State.ShouldBe(ConnectionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Endpoints should mirror across the pair")]
    public void Create_EndPoints_ShouldMirrorAcrossPair()
    {
        // Act
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Assert
        client.LocalEndPoint.ShouldNotBeNull();
        client.RemoteEndPoint.ShouldNotBeNull();
        client.LocalEndPoint.ShouldBe(server.RemoteEndPoint);
        client.RemoteEndPoint.ShouldBe(server.LocalEndPoint);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Ids should be unique and non-empty")]
    public void Create_Ids_ShouldBeUniqueAndNonEmpty()
    {
        // Act
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Assert
        client.Id.ShouldNotBe(ConnectionId.Empty);
        server.Id.ShouldNotBe(ConnectionId.Empty);
        client.Id.ShouldNotBe(server.Id);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Should advertise the Memory protocol and be bidirectional")]
    public void Create_Capabilities_ShouldAdvertiseMemoryProtocol()
    {
        // Act
        (Connection client, Connection server) = InMemoryConnectionPair.Create();

        // Assert
        client.Capabilities.Protocol.ShouldBe(ConnectionProtocol.Memory);
        client.Capabilities.IsReliable.ShouldBeTrue();
        client.Capabilities.IsOrdered.ShouldBeTrue();
        client.Capabilities.IsMultiplexed.ShouldBeFalse();
        client.Direction.ShouldBe(ConnectionDirection.Bidirectional);
        server.Direction.ShouldBe(ConnectionDirection.Bidirectional);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.InMemory] - Pair: Custom capabilities should flow to both ends")]
    public void Create_WithCustomCapabilities_ShouldApplyToBothEnds()
    {
        // Arrange
        ConnectionCapabilities capabilities = InMemoryConnectionPair.DefaultCapabilities with
        {
            Security = ConnectionSecurity.Tls
        };

        // Act
        (Connection client, Connection server) = InMemoryConnectionPair.Create(capabilities);

        // Assert
        client.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
        server.Capabilities.Security.ShouldBe(ConnectionSecurity.Tls);
    }
}
