using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Validates encoder-stream instructions against the advertised zero dynamic-table capacity.</summary>
public class Http3QPackEncoderStreamTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 QPACK: A disabled-table encoder instruction should close with QPACK_ENCODER_STREAM_ERROR")]
    [InlineData(new byte[] { 0xC0, 0x00 })]       // Insert with a static name reference and empty value.
    [InlineData(new byte[] { 0x80, 0x00 })]       // Insert with a dynamic name reference and empty value.
    [InlineData(new byte[] { 0x40, 0x00 })]       // Insert with an empty literal name and value.
    [InlineData(new byte[] { 0x00 })]             // Duplicate entry 0 from the empty table.
    [InlineData(new byte[] { 0x21 })]             // Set Dynamic Table Capacity to 1.
    [InlineData(new byte[] { 0x3F, 0x61 })]       // Set Dynamic Table Capacity to 128.
    [InlineData(new byte[] { 0x20, 0x20, 0x21 })] // Legal zero capacities followed by an illegal capacity.
    public async Task ReceiveAsync_OnDisabledTableInstruction_ShouldAbortWithEncoderStreamError(byte[] instruction)
    {
        // Arrange — serve a request to start the live accept loop; keep the encoder stream open so
        // the instruction, rather than a critical-stream FIN, must cause the connection error.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CodeRecordingMultiplexedConnection? recorded = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => recorded = new CodeRecordingMultiplexedConnection(connection));
        await ServeRequestAsync(peer, "/ready", timeout.Token);
        await using Connection encoder = await peer.OpenUnidirectionalStreamAsync();

        // Act
        await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3QPackEncoderStream(instruction), timeout.Token);
        await WaitForCloseAsync(peer, timeout.Token);

        // Assert
        recorded.ShouldNotBeNull();
        recorded.AbortErrorCode.ShouldBe((long)Http3ErrorCode.QPackEncoderStreamError);
        recorded.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.QPackEncoderStreamError);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 QPACK: A disabled-table instruction prefix should be rejected without buffering its missing payload")]
    [InlineData((byte)0xFF)] // Insert with Name Reference, before even its extended index arrives.
    [InlineData((byte)0x5F)] // Insert with Literal Name, before its extended string length arrives.
    [InlineData((byte)0x7F)] // Insert with Huffman literal name, before its extended string length arrives.
    [InlineData((byte)0x3F)] // Set Capacity, already at least 31 before its continuation arrives.
    public async Task ReceiveAsync_OnDisabledTableIncompleteInstruction_ShouldRejectWithoutWaitingForPayload(byte prefix)
    {
        // Arrange — a sender can stall forever after an invalid instruction prefix. The disabled
        // path must decide from that prefix, without allocating a buffer for a declared name/value.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CodeRecordingMultiplexedConnection? recorded = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => recorded = new CodeRecordingMultiplexedConnection(connection));
        await ServeRequestAsync(peer, "/ready", timeout.Token);
        await using Connection encoder = await peer.OpenUnidirectionalStreamAsync();

        // Act — no instruction continuation and no FIN follow this byte.
        await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3QPackEncoderStream([prefix]), timeout.Token);
        await WaitForCloseAsync(peer, timeout.Token);

        // Assert
        recorded.ShouldNotBeNull();
        recorded.AbortErrorCode.ShouldBe((long)Http3ErrorCode.QPackEncoderStreamError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 QPACK: Repeated zero-capacity instructions should remain valid across encoder writes")]
    public async Task ReceiveAsync_OnRepeatedZeroCapacity_ShouldKeepServingUntilEncoderFin()
    {
        // Arrange — accepted behavior already holds. Separate writes interleave with complete
        // exchanges, exercising the connection-lived drain as further instructions arrive.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        CodeRecordingMultiplexedConnection? recorded = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => recorded = new CodeRecordingMultiplexedConnection(connection));
        await ServeRequestAsync(peer, "/ready", timeout.Token);
        await using Connection encoder = await peer.OpenUnidirectionalStreamAsync();
        await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3QPackEncoderStream(), timeout.Token);

        // Act
        for (int index = 0; index < 3; index++)
        {
            await encoder.Output.WriteAsync(HttpProtocolPayloadFactory.QPackSetCapacity(0), timeout.Token);
            await ServeRequestAsync(peer, $"/capacity-{index}", timeout.Token);
        }

        encoder.Output.Complete();
        await WaitForCloseAsync(peer, timeout.Token);

        // Assert — FIN establishes that the drain consumed every preceding zero without error.
        recorded.ShouldNotBeNull();
        recorded.AbortErrorCode.ShouldBe((long)Http3ErrorCode.ClosedCriticalStream);
    }

    private static async Task ServeRequestAsync(Http3InMemoryPeer peer, string path, CancellationToken cancellationToken)
    {
        await using Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", path, "https", "a"), cancellationToken);
        request.Output.Complete();
        await using IHttpContext exchange = await peer.NextContextAsync();
        exchange.Request.Path.Value.ShouldBe(path);
        exchange.Response.StatusCode = HttpStatusCode.Ok;
        await peer.ConnectionContext.SendAsync(exchange, cancellationToken);
        (await Http3InMemoryPeer.ReadToEndAsync(request)).ShouldNotBeEmpty();
        peer.Server.State.ShouldBe(ConnectionState.Open);
    }

    private static async Task WaitForCloseAsync(Http3InMemoryPeer peer, CancellationToken cancellationToken)
    {
        Stopwatch wait = Stopwatch.StartNew();
        while (!peer.Server.ConnectionClosed.IsCancellationRequested && wait.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
