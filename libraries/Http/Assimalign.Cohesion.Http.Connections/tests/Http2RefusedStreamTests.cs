using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 streams the server refuses (#1317): over <c>SETTINGS_MAX_CONCURRENT_STREAMS</c>, or while a
/// graceful close drains. The refused stream's header block is still HPACK-decoded before the
/// <c>RST_STREAM(REFUSED_STREAM)</c> goes out (RFC 9113 §4.3), and its id counts as seen, so the
/// peer's later frames on it are frames on a closed stream rather than an idle one (RFC 9113 §5.1.1).
/// </summary>
public class Http2RefusedStreamTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Refused Streams: A refused stream that indexes a field should not break a later request that references it")]
    public async Task ReceiveAsync_OnRefusedStreamThatIndexesField_ShouldDecodeLaterRequestThatReferencesIt()
    {
        // Arrange — one concurrent stream is allowed, and stream 1 holds the slot.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/first"));
        IHttpContext first = await peer.ReceiveContextAsync();

        // Act — stream 3 is refused, and its head adds x-refused to the dynamic table. Once stream 1
        // is answered, stream 5's head references that entry.
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            3,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/refused"),
                HPackTestEncoder.LiteralWithIndexing("x-refused", "yes"))));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");
        await peer.ConnectionContext.SendAsync(first).AsTask().WaitAsync(_timeout);
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            5,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/next"),
                HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex))));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert — refused, yet decoded: the reference resolves.
        peer.Output.ForStream(3).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);
        next.Request.Path.Value.ShouldBe("/next");
        next.Request.Headers[new HttpHeaderKey("x-refused")].Value.ShouldBe("yes");

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Refused Streams: A stream refused during a graceful close should still be decoded")]
    public async Task ReceiveAsync_OnStreamRefusedDuringGracefulClose_ShouldKeepDecoderInStep()
    {
        // Arrange — stream 1 is accepted and still sending its body when the close begins.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext upload = await peer.ReceiveContextAsync();
        peer.ConnectionContext.BeginGracefulClose();

        // Act — stream 3 arrives after the close began and is refused; its head indexes a field that
        // stream 1's trailer section then references.
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            3,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/late"),
                HPackTestEncoder.LiteralWithIndexing("x-checksum", "abc123"))));
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex)));

        using StreamReader reader = new(upload.Request.Body, leaveOpen: true);
        await reader.ReadToEndAsync().WaitAsync(_timeout);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");

        // Assert
        peer.Output.ForStream(3).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.RefusedStream);
        upload.Request.Trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");

        await peer.ConnectionContext.SendAsync(upload).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Refused Streams: DATA on a refused stream should not close the connection")]
    public async Task ReceiveAsync_OnDataForRefusedStream_ShouldKeepConnectionOpen()
    {
        // Arrange — stream 3 is refused while the client still has its body to send.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/first"));
        IHttpContext first = await peer.ReceiveContextAsync();
        await peer.SendHeadersAsync(3, endStream: false, Http2TestPeer.Request("POST", "/refused"));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");

        // Act — the body the client sent before it saw the refusal arrives.
        await peer.SendDataAsync(3, Http2TestPeer.CreateBody(100), endStream: true);
        await peer.SyncAsync();

        // Assert — the refused id counts as seen, so the DATA is not on an idle stream (RFC 9113 §5.1.1).
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        await peer.ConnectionContext.SendAsync(first).AsTask().WaitAsync(_timeout);
        await peer.SendHeadersAsync(5, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Path.Value.ShouldBe("/next");

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Refused Streams: GOAWAY should name the last accepted stream, not a refused one")]
    public async Task ReceiveAsync_OnConnectionErrorAfterRefusal_ShouldGoAwayWithLastAcceptedStream()
    {
        // Arrange — stream 1 is accepted and stream 3 refused.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: static http2 => http2.Limits.MaxStreamsPerConnection = 1);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/first"));
        IHttpContext first = await peer.ReceiveContextAsync();
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/refused"));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 3),
            "the refusal of stream 3");

        // Act — a PING on a stream is a connection error (RFC 9113 §6.7).
        await peer.SendAsync(Http2TestSettings.RawFrame(0x6, 0, 1, new byte[8]));
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsGoAway), "the GOAWAY");

        // Assert — RFC 9113 §6.8: the last stream the server might act on is stream 1, not refused stream 3.
        Http2WireFrame goAway = peer.Output.Frames.Single(frame => frame.IsGoAway);
        goAway.GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(goAway.Payload) & 0x7FFFFFFF).ShouldBe(1);

        await peer.ConnectionContext.SendAsync(first).AsTask().WaitAsync(_timeout);
    }
}
