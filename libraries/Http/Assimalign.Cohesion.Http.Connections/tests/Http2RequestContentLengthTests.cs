using System;
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
/// An HTTP/2 request's declared <c>content-length</c> binds its content (RFC 9113 §8.1.1): DATA that runs
/// past it, or a stream that ends short of it, makes the request malformed, so its stream alone is reset
/// with <c>PROTOCOL_ERROR</c> and a read of its body fails. A value that is not a valid length, or a
/// non-zero length on a head that ends the stream, is malformed before the request is dispatched, as on
/// HTTP/3 (RFC 9114 §4.1.2).
/// </summary>
public class Http2RequestContentLengthTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: DATA past the declared content-length should reset the stream with PROTOCOL_ERROR")]
    public async Task ReceiveData_OnContentPastDeclaredLength_ShouldResetWithProtocolError()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "5")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — eight octets against a declaration of five.
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("12345678"), endStream: true);

        // Assert
        await AssertResetAsync(peer, streamId: 1);
        await ShouldFailToReadAsync(exchange.Request.Body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: A stream that ends short of the declared content-length should reset with PROTOCOL_ERROR")]
    public async Task ReceiveData_OnEndStreamShortOfDeclaredLength_ShouldResetWithProtocolError()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "100")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — the stream ends after five of the hundred declared octets.
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: true);

        // Assert — the short body never reads as complete.
        await AssertResetAsync(peer, streamId: 1);
        await ShouldFailToReadAsync(exchange.Request.Body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: A trailer section that ends the stream short of the declared content-length should reset with PROTOCOL_ERROR")]
    public async Task ReceiveTrailers_OnContentShortOfDeclaredLength_ShouldResetWithProtocolError()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "10")));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — a valid trailer section ends the stream after five of the ten declared octets.
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.Literal("x-checksum", "abc123")));

        // Assert
        await AssertResetAsync(peer, streamId: 1);
        await ShouldFailToReadAsync(exchange.Request.Body);
        exchange.Request.Trailers.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: Content that adds up to the declared content-length should be delivered in full")]
    public async Task ReceiveData_OnContentMatchingDeclaredLength_ShouldDeliverBody()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "11")));
        IHttpContext exchange = await peer.ReceiveContextAsync();

        // Act — split across two DATA frames, then a trailer section.
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello "), endStream: false);
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("world"), endStream: false);
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.Literal("x-checksum", "abc123")));
        string body = await ReadToEndAsync(exchange.Request.Body);
        await peer.SyncAsync();

        // Assert
        body.ShouldBe("hello world");
        exchange.Request.Trailers[new HttpHeaderKey("x-checksum")].Value.ShouldBe("abc123");
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: A head that is malformed for its content-length should reset only its stream before dispatch")]
    [InlineData("5", true)]    // §8.1.1: content declared, but the head ends the stream
    [InlineData("5x", false)]  // RFC 9110 §8.6: not a decimal integer
    [InlineData("5, 6", false)] // RFC 9110 §8.6: conflicting values
    [InlineData("-1", false)]  // RFC 9110 §8.6: not non-negative
    public async Task ReceiveHeaders_OnMalformedContentLength_ShouldResetBeforeDispatch(string contentLength, bool endStream)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream, Http2TestPeer.Request("POST", "/malformed", ("content-length", contentLength)));
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert — the first exchange dispatched is stream 3's, and the connection kept serving.
        next.Request.Path.Value.ShouldBe("/next");
        await AssertResetAsync(peer, streamId: 1);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Content-Length: A content-length of zero on a head that ends the stream should be dispatched")]
    public async Task ReceiveHeaders_OnZeroContentLengthWithEndStream_ShouldDispatch()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Request("POST", "/empty", ("content-length", "0")));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        string body = await ReadToEndAsync(exchange.Request.Body);

        // Assert
        exchange.Request.Path.Value.ShouldBe("/empty");
        body.ShouldBeEmpty();

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsRstStream);
    }

    private static async Task AssertResetAsync(Http2TestPeer peer, int streamId)
    {
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == streamId),
            $"the reset of stream {streamId}");
        peer.Output.ForStream(streamId).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
    }

    private static async Task<string> ReadToEndAsync(Stream body)
    {
        using StreamReader reader = new(body, leaveOpen: true);
        return await reader.ReadToEndAsync().WaitAsync(_timeout);
    }

    private static async Task ShouldFailToReadAsync(Stream body)
    {
        // The read fails with the malformed-content condition or, once the reset landed, as the request
        // abort; either way it never reads the request as complete.
        Exception? failure = null;

        try
        {
            await ReadToEndAsync(body);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            failure = exception;
        }

        failure.ShouldNotBeNull("reading the body of a request whose content contradicts its content-length must fail");
    }
}
