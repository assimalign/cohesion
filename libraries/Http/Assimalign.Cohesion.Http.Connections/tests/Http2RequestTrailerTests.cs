using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 request trailers (#1314, RFC 9113 §8.1): every trailer section is HPACK-decoded as it
/// arrives — whether or not the application reads it, and even after the server reset the stream —
/// so the connection's decoder stays in step (RFC 9113 §4.3, RFC 7541). A valid section surfaces on
/// <c>Request.Trailers</c> once the body is read to its end; a malformed one resets its stream only.
/// </summary>
/// <remarks>
/// Every exchange a test leaves standing is answered before the peer is disposed, so the connection's
/// graceful close does not wait out its drain window for it.
/// </remarks>
public class Http2RequestTrailerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer field added to the dynamic table should decode a later request that references it")]
    public async Task ReceiveAsync_OnTrailerAddedToDynamicTable_ShouldDecodeLaterRequestThatReferencesIt()
    {
        // Arrange — the first request's trailer section adds x-checksum to the HPACK dynamic table, and
        // its handler never reads the body or the trailers.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.LiteralWithIndexing("x-checksum", "abc123")));
        IHttpContext upload = await peer.ReceiveContextAsync();

        // Act — the second request's head references that entry by index.
        await peer.SendAsync(NextRequestReferencingNewestEntry(3));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert — the server's decoder applied the insertion, so the reference resolves.
        upload.Request.Path.Value.ShouldBe("/upload");
        next.Request.Path.Value.ShouldBe("/next");
        next.Request.Headers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");

        await RespondAsync(peer, upload, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer section that arrives after the server reset its stream should be decoded and ignored")]
    public async Task ReceiveAsync_OnTrailersAfterServerResetStream_ShouldDecodeAndIgnoreThem()
    {
        // Arrange — the handler answers without reading the body, so the server resets the stream with
        // NO_ERROR while the client is still sending (RFC 9113 §8.1).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext upload = await peer.ReceiveContextAsync();
        await RespondAsync(peer, upload);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");

        // Act — the client's trailer section was already in flight, and it adds to the dynamic table. A
        // new request then references the entry.
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.LiteralWithIndexing("x-checksum", "abc123")));
        await peer.SendAsync(NextRequestReferencingNewestEntry(3));
        IHttpContext next = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert — the trailer section was not mistaken for a new request on stream 1, its insertion was
        // applied, and the server ignored it rather than resetting stream 1 again or closing the connection.
        next.Request.Path.Value.ShouldBe("/next");
        next.Request.Headers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
        peer.Output.ForStream(1).Count(frame => frame.IsRstStream).ShouldBe(1);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await RespondAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer section still arriving when its response completes should be decoded once its CONTINUATION ends it")]
    public async Task ReceiveAsync_OnTrailerContinuationAfterResponse_ShouldDecodeWholeSection()
    {
        // Arrange — the trailer section opens (HEADERS with END_STREAM, no END_HEADERS) and adds a field
        // to the dynamic table. The handler answers before the section's CONTINUATION arrives, which
        // retires the stream: closed, or reset if the pump had not yet read the HEADERS frame.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext upload = await peer.ReceiveContextAsync();
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            1,
            endStream: true,
            HPackTestEncoder.LiteralWithIndexing("x-checksum", "abc123"),
            endHeaders: false));
        await Task.Delay(100);
        await RespondAsync(peer, upload);
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1 && frame.EndStream),
            "the response on stream 1");

        // Act — the section ends, and a new request references the entry it added.
        await peer.SendAsync(HPackTestEncoder.ContinuationFrame(1, HPackTestEncoder.Literal("x-more", "yes"), endHeaders: true));
        await peer.SendAsync(NextRequestReferencingNewestEntry(3));
        IHttpContext next = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert — the block was completed and decoded on the retired stream, and the connection stayed up.
        next.Request.Headers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await RespondAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: Trailers should surface on Request.Trailers once the body is read to its end")]
    public async Task ReadBody_OnTrailingHeaders_ShouldSurfaceTrailersAtEndOfBody()
    {
        // Arrange — the whole request, trailer section included, has been processed by the frame pump.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(
            1,
            HPackTestEncoder.Literal("x-checksum", "abc123"),
            HPackTestEncoder.Literal("x-item", "first"),
            HPackTestEncoder.Literal("x-item", "second")));
        IHttpContext context = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        IHttpTrailerCollection trailers = context.Request.Trailers;
        trailers.IsSupported.ShouldBeTrue();
        trailers.Count.ShouldBe(0);

        // Act
        string body = await ReadToEndAsync(context.Request.Body);

        // Assert — like HTTP/1.1 and HTTP/3, the section is published when the reader reaches the end.
        body.ShouldBe("hello");
        trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
        trailers[new HttpHeaderKey("x-item")].Count.ShouldBe(2);
        context.Request.Headers.ContainsKey(new HttpHeaderKey("x-checksum")).ShouldBeFalse();

        await RespondAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer section split across CONTINUATION frames should be decoded once complete")]
    public async Task ReadBody_OnTrailersSplitAcrossContinuation_ShouldSurfaceWholeSection()
    {
        // Arrange — the trailing HEADERS frame carries END_STREAM, and the block ends in a CONTINUATION.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        await peer.SendAsync(
            HPackTestEncoder.HeadersFrame(1, endStream: true, HPackTestEncoder.LiteralWithIndexing("x-first", "one"), endHeaders: false),
            HPackTestEncoder.ContinuationFrame(1, HPackTestEncoder.Literal("x-second", "two"), endHeaders: true));
        IHttpContext context = await peer.ReceiveContextAsync();

        // Act
        string body = await ReadToEndAsync(context.Request.Body);

        // Assert
        body.ShouldBe("hello");
        context.Request.Trailers[new HttpHeaderKey("x-first")].Value.ShouldBe("one");
        context.Request.Trailers[new HttpHeaderKey("x-second")].Value.ShouldBe("two");

        await RespondAsync(peer, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A malformed trailer section should reset only its stream and keep HPACK in step")]
    [InlineData(":path")]
    [InlineData(":status")]
    [InlineData("content-length")]
    [InlineData("host")]
    [InlineData("authorization")]
    [InlineData("trailer")]
    [InlineData("connection")]
    [InlineData("X-Checksum")]
    public async Task ReceiveAsync_OnMalformedTrailerSection_ShouldResetStreamAndKeepDecoderInStep(string fieldName)
    {
        // Arrange — the trailer section first adds a field to the dynamic table, then carries a field a
        // trailer section may not: a pseudo-header, a field RFC 9110 §6.5.1 prohibits, a
        // connection-specific field, or an uppercase name.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        IHttpContext upload = await peer.ReceiveContextAsync();

        // Act
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(
            1,
            HPackTestEncoder.LiteralWithIndexing("x-kept", "decoded"),
            HPackTestEncoder.Literal(fieldName, "value")));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");

        // Assert — a stream error of type PROTOCOL_ERROR (RFC 9113 §8.1.1), and the handler's body read fails.
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        await ShouldFailToReadBodyAsync(upload.Request.Body);

        // The whole block was decoded before it was judged, so the insertion it carried still applies.
        await peer.SendAsync(NextRequestReferencingNewestEntry(3));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Headers[new HttpHeaderKey("x-kept")].Value.ShouldBe("decoded");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await RespondAsync(peer, next);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer section on a CONNECT stream should reset the stream")]
    public async Task ReceiveAsync_OnTrailersAfterConnect_ShouldResetStreamWithProtocolError()
    {
        // Arrange — RFC 9113 §8.5: after a CONNECT head, the stream carries only DATA.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, (":method", "CONNECT"), (":authority", "upstream.test:443"));
        IHttpContext tunnel = await peer.ReceiveContextAsync();

        // Act
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.Literal("x-checksum", "abc123")));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");

        // Assert
        tunnel.Request.Method.ShouldBe(HttpMethod.Connect);
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Request Trailers: A trailer section over the header-list limit should close the connection with ENHANCE_YOUR_CALM")]
    public async Task ReceiveAsync_OnTrailersOverHeaderListLimit_ShouldGoAwayEnhanceYourCalm()
    {
        // Arrange — the head adds a 137-octet entry (5 + 100 + 32) to the dynamic table; the trailer
        // section references it four times: 4 encoded octets that decode to 548 against a 512 limit
        // (RFC 9113 §10.5.1, SETTINGS_MAX_HEADER_LIST_SIZE).
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: http2 => http2.Limits.MaxRequestHeaderListSize = 512);
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            1,
            endStream: false,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("POST", "/upload"),
                HPackTestEncoder.LiteralWithIndexing("x-pad", new string('a', 100)))));
        await peer.ReceiveContextAsync();
        byte[] reference = HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex);

        // Act
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, reference, reference, reference, reference));
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            observed => observed.Any(frame => frame.IsGoAway),
            "the GOAWAY");

        // Assert — the decode aborts at the limit, so the decoder state is lost: a connection error.
        frames.Single(frame => frame.IsGoAway).GetGoAwayErrorCode().ShouldBe(Http2ErrorCode.EnhanceYourCalm);
    }

    /// <summary>
    /// A bodyless GET for <c>/next</c> whose head references the newest dynamic-table entry by index.
    /// </summary>
    private static byte[] NextRequestReferencingNewestEntry(int streamId)
    {
        return HPackTestEncoder.HeadersFrame(
            streamId,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/next"),
                HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex)));
    }

    private static async Task RespondAsync(Http2TestPeer peer, params IHttpContext[] exchanges)
    {
        foreach (IHttpContext exchange in exchanges)
        {
            await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        }
    }

    private static async Task<string> ReadToEndAsync(Stream body)
    {
        using StreamReader reader = new(body, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(_timeout);
    }

    private static async Task ShouldFailToReadBodyAsync(Stream body)
    {
        // The read fails with the malformed-trailers condition itself or — once the reset landed — as the
        // request abort; either way the handler never mistakes the request for a complete one.
        Exception? failure = null;

        try
        {
            await ReadToEndAsync(body);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            failure = exception;
        }

        failure.ShouldNotBeNull("reading the body of a request whose trailer section is malformed must fail");
    }
}
