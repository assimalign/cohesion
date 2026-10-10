using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Frames that arrive on an HTTP/2 stream after the server reset it (#1318, #1074). The peer may have
/// sent them before the reset reached it, so RFC 9113 §5.1 has the server ignore them — no RST_STREAM
/// in reply — whether or not the peer was still sending when the stream was reset, while DATA still
/// counts against the connection window and is credited back (RFC 9113 §6.9).
/// </summary>
public class Http2ResetStreamTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: DATA after an early response and reset should be ignored and its window credited back")]
    public async Task ReceiveAsync_OnDataAfterServerResetStream_ShouldIgnoreItAndCreditConnectionWindow()
    {
        // Arrange — the handler answers before reading the body, so the server resets stream 1 with
        // NO_ERROR while the client is still sending (RFC 9113 §8.1).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await ResetWhileClientSendsAsync(peer, 1);
        int creditBefore = ConnectionCredit(peer);

        // Act — the body the client sent before it saw the reset arrives.
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(1000), endStream: false);
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(1000), endStream: true);
        await peer.SyncAsync();

        // Assert — no RST_STREAM in reply, and the 2000 octets went back to the connection window.
        peer.Output.ForStream(1).Count(frame => frame.IsRstStream).ShouldBe(1);
        (ConnectionCredit(peer) - creditBefore).ShouldBe(2000);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: Ignored DATA should leave the connection window usable")]
    public async Task ReceiveAsync_OnLargeIgnoredData_ShouldLeaveConnectionWindowUsable()
    {
        // Arrange — 60000 octets arrive on reset stream 1; the connection window is 65535.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await ResetWhileClientSendsAsync(peer, 1);

        for (int frame = 0; frame < 4; frame++)
        {
            await peer.SendDataAsync(1, Http2TestPeer.CreateBody(15000), endStream: frame == 3);
        }

        // Act — another 60000 octets on a new stream fit only if the ignored octets were credited back.
        await peer.SendHeadersAsync(3, endStream: false, Http2TestPeer.Request("POST", "/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        for (int frame = 0; frame < 4; frame++)
        {
            await peer.SendDataAsync(3, Http2TestPeer.CreateBody(15000), endStream: frame == 3);
        }

        using MemoryStream received = new();
        await next.Request.Body.CopyToAsync(received).WaitAsync(_timeout);

        // Assert
        received.Length.ShouldBe(60000);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: A WINDOW_UPDATE on a reset stream should be ignored, even with a zero increment")]
    public async Task ReceiveAsync_OnZeroWindowUpdateAfterServerResetStream_ShouldIgnoreIt()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await ResetWhileClientSendsAsync(peer, 1);

        // Act — on a live stream a zero increment is a stream error (RFC 9113 §6.9).
        await peer.SendWindowUpdateAsync(1, 0);
        await peer.SendWindowUpdateAsync(1, 1024);
        await peer.SyncAsync();

        // Assert
        peer.Output.ForStream(1).Count(frame => frame.IsRstStream).ShouldBe(1);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: A WINDOW_UPDATE on a stream reset after its request ended should be ignored, even with a zero increment")]
    public async Task ReceiveAsync_OnZeroWindowUpdateAfterServerResetOfEndedRequest_ShouldIgnoreIt()
    {
        // Arrange — the request ended with its HEADERS, and the application cancels the exchange, so
        // the server resets stream 1 with CANCEL. The client is still receiving the response, so it
        // may still credit the stream.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await ResetEndedRequestAsync(peer, 1);

        // Act — on a live stream a zero increment is a stream error (RFC 9113 §6.9).
        await peer.SendWindowUpdateAsync(1, 0);
        await peer.SendWindowUpdateAsync(1, 1024);
        await peer.SyncAsync();

        // Assert — RFC 9113 §5.1: no second RST_STREAM on a stream the server already reset.
        peer.Output.ForStream(1).Count(frame => frame.IsRstStream).ShouldBe(1);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: A zero WINDOW_UPDATE on a stream both sides ended should be ignored")]
    public async Task ReceiveAsync_OnZeroWindowUpdateAfterCompletedExchange_ShouldIgnoreIt()
    {
        // Arrange — stream 1 is answered in full, so both sides have ended it.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/done"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);

        // Act
        await peer.SendWindowUpdateAsync(1, 0);
        await peer.SyncAsync();

        // Assert — RFC 9113 §5.1: a closed stream ignores WINDOW_UPDATE, and nothing may be sent on it.
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Reset Streams: DATA on a stream reset after its request ended should be ignored and its window credited back")]
    public async Task ReceiveAsync_OnDataAfterServerResetOfEndedRequest_ShouldIgnoreItAndCreditConnectionWindow()
    {
        // Arrange — the server reset stream 1 after the client ended its request.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await ResetEndedRequestAsync(peer, 1);
        int creditBefore = ConnectionCredit(peer);

        // Act
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(1000), endStream: false);
        await peer.SyncAsync();

        // Assert — RFC 9113 §5.1: the server ignores frames on a stream it reset, and sends nothing on
        // it; the octets still go back to the connection window (RFC 9113 §6.9).
        peer.Output.ForStream(1).Count(frame => frame.IsRstStream).ShouldBe(1);
        (ConnectionCredit(peer) - creditBefore).ShouldBe(1000);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    /// <summary>
    /// Opens <paramref name="streamId"/> with a request that ends with its HEADERS, cancels the exchange,
    /// and finalizes it, so the server resets the stream with <c>CANCEL</c>; waits for that reset.
    /// </summary>
    private static async Task ResetEndedRequestAsync(Http2TestPeer peer, int streamId)
    {
        await peer.SendHeadersAsync(streamId, endStream: true, Http2TestPeer.Get("/cancelled"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Cancel();
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == streamId),
            $"the reset of stream {streamId}");
    }

    /// <summary>
    /// Opens <paramref name="streamId"/> with a request whose body is still to come and answers it at
    /// once, so the server resets the stream with <c>NO_ERROR</c>; waits for that reset.
    /// </summary>
    private static async Task ResetWhileClientSendsAsync(Http2TestPeer peer, int streamId)
    {
        await peer.SendHeadersAsync(streamId, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext upload = await peer.ReceiveContextAsync();
        await peer.ConnectionContext.SendAsync(upload).AsTask().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == streamId),
            $"the reset of stream {streamId}");
    }

    /// <summary>The connection-level credit the server has granted so far: WINDOW_UPDATE frames on stream 0.</summary>
    private static int ConnectionCredit(Http2TestPeer peer)
    {
        return peer.Output.Frames
            .Where(frame => frame.Type == 0x8 && frame.StreamId == 0)
            .Sum(frame => (int)(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) & 0x7FFFFFFF));
    }
}
