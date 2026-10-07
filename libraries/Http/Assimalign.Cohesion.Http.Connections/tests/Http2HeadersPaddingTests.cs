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
/// Padded HTTP/2 HEADERS frames (#1320, RFC 9113 §6.2): the Pad Length octet, the PRIORITY fields and
/// the trailing padding are framing, so only the field block fragment reaches the HPACK decoder, and
/// padding longer than the octets left for the fragment is a connection PROTOCOL_ERROR.
/// </summary>
public class Http2HeadersPaddingTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private const byte endStreamFlag = 0x1;
    private const byte endHeadersFlag = 0x4;
    private const byte paddedFlag = 0x8;
    private const byte priorityFlag = 0x20;

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEADERS Padding: A padded request head should decode as if it were unpadded")]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(255, false)]
    [InlineData(7, true)]
    public async Task ReceiveAsync_OnPaddedRequestHead_ShouldDecodeFieldBlockOnly(int padLength, bool withPriority)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        byte[] block = HPackTestEncoder.Block(
            HPackTestEncoder.RequestHead("GET", "/padded"),
            HPackTestEncoder.Literal("x-trace", "abc"));

        // Act
        await peer.SendAsync(PaddedHeadersFrame(1, endHeadersFlag | endStreamFlag, block, padLength, withPriority));
        IHttpContext context = await peer.ReceiveContextAsync();

        // Assert
        context.Request.Path.Value.ShouldBe("/padded");
        context.Request.Headers[new HttpHeaderKey("x-trace")].Value.ShouldBe("abc");

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEADERS Padding: A padded trailer section should decode as if it were unpadded")]
    public async Task ReadBody_OnPaddedTrailerSection_ShouldSurfaceTrailersAndKeepDecoderInStep()
    {
        // Arrange — the padded trailer section adds x-checksum to the dynamic table.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        await peer.SendAsync(PaddedHeadersFrame(
            1,
            endHeadersFlag | endStreamFlag,
            HPackTestEncoder.LiteralWithIndexing("x-checksum", "abc123"),
            padLength: 16,
            withPriority: false));
        IHttpContext upload = await peer.ReceiveContextAsync();

        // Act
        using StreamReader reader = new(upload.Request.Body, leaveOpen: true);
        string body = await reader.ReadToEndAsync().WaitAsync(_timeout);
        await peer.SendAsync(HPackTestEncoder.HeadersFrame(
            3,
            endStream: true,
            HPackTestEncoder.Block(
                HPackTestEncoder.RequestHead("GET", "/next"),
                HPackTestEncoder.Indexed(HPackTestEncoder.NewestDynamicIndex))));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        body.ShouldBe("hello");
        upload.Request.Trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
        next.Request.Headers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");

        await peer.ConnectionContext.SendAsync(upload).AsTask().WaitAsync(_timeout);
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEADERS Padding: Padding that fills the frame should leave an empty fragment for CONTINUATION to complete")]
    public async Task ReceiveAsync_OnPaddingFillingTheFrame_ShouldAcceptEmptyFragment()
    {
        // Arrange — RFC 9113 §6.2 rejects padding that exceeds the octets left for the fragment, so
        // padding that fills them leaves an empty fragment and the CONTINUATION carries the block.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendAsync(PaddedHeadersFrame(1, endStreamFlag, Array.Empty<byte>(), padLength: 9, withPriority: false));
        await peer.SendAsync(Http2TestSettings.RawFrame(0x9, endHeadersFlag, 1, HPackTestEncoder.RequestHead("GET", "/continued")));
        IHttpContext context = await peer.ReceiveContextAsync();

        // Assert
        context.Request.Path.Value.ShouldBe("/continued");

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 HEADERS Padding: Padding longer than the octets left for the fragment should close the connection with PROTOCOL_ERROR")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiveAsync_OnPaddingLongerThanFrame_ShouldGoAwayProtocolError(bool withPriority)
    {
        // Arrange — the frame declares 200 octets of padding but carries only its field block.
        List<byte> headersPayload = new() { 200 };

        if (withPriority)
        {
            headersPayload.AddRange(new byte[] { 0, 0, 0, 0, 15 });
        }

        headersPayload.AddRange(HPackTestEncoder.RequestHead("GET", "/oversized"));
        byte flags = (byte)(endHeadersFlag | endStreamFlag | paddedFlag | (withPriority ? priorityFlag : 0));
        byte[][] wire =
        [
            Http2TestSettings.Preface(),
            Http2TestSettings.RawFrame(0x4, 0, 0, Array.Empty<byte>()),
            Http2TestSettings.RawFrame(0x1, flags, 1, headersPayload.ToArray()),
        ];
        TestConnection connection = new(wire.SelectMany(part => part).ToArray());
        HttpConnectionListenerOptions options = new();
        options.UseHttp2(new TestConnectionListener(connection));
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connectionContext = await (await listener.AcceptOrListenAsync()).OpenAsync();
        int dispatched = 0;

        // Act — the receive loop absorbs the connection error.
        await foreach (IHttpContext _ in connectionContext.ReceiveAsync())
        {
            dispatched++;
        }

        // Assert — RFC 9113 §6.2: padding that exceeds the octets left for the fragment.
        dispatched.ShouldBe(0);
        Http2TestSettings.AssertContainsGoAway(await connection.ReadOutputAsync(), Http2ErrorCode.ProtocolError);
    }

    /// <summary>
    /// A HEADERS frame with the PADDED flag (and the PRIORITY flag when requested): the Pad Length
    /// octet, the five PRIORITY octets, the field block, then <paramref name="padLength"/> zero octets.
    /// </summary>
    private static byte[] PaddedHeadersFrame(int streamId, int flags, byte[] block, int padLength, bool withPriority)
    {
        List<byte> payload = new() { (byte)padLength };

        if (withPriority)
        {
            // Exclusive bit clear, stream dependency 0, weight 15 (RFC 9113 §6.2).
            payload.AddRange(new byte[] { 0, 0, 0, 0, 15 });
        }

        payload.AddRange(block);
        payload.AddRange(new byte[padLength]);

        byte frameFlags = (byte)(flags | paddedFlag | (withPriority ? priorityFlag : 0));
        return Http2TestSettings.RawFrame(0x1, frameFlags, streamId, payload.ToArray());
    }
}
