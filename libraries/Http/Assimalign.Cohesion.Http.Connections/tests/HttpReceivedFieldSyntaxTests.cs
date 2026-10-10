using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// HTTP/2 and HTTP/3 apply the core field rule to every field line they receive, in a head and in a
/// trailer section (#1376, RFC 9113 §8.2.1, RFC 9114 §4.2): a name is a lowercase token, and a value
/// has no NUL, CR, LF, or other control character but HTAB, and no SP or HTAB at either end. A violation
/// makes the request malformed — a stream error, <c>PROTOCOL_ERROR</c> on HTTP/2 and
/// <c>H3_MESSAGE_ERROR</c> on HTTP/3 — that costs the request alone: a sibling stream is served, and the
/// connection stays open.
/// </summary>
public class HttpReceivedFieldSyntaxTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>Regular fields whose name or value breaks the rule.</summary>
    public static TheoryData<string, string> InvalidFields => new()
    {
        { "x-echo", "a\r\nx-injected: 1" },   // CR LF
        { "x-echo", "a\nx-injected: 1" },     // bare LF
        { "x-echo", "a\rb" },                 // bare CR
        { "x-echo", "a\0b" },                 // NUL
        { "x-echo", "a\u0001b" },             // another control character
        { "x-echo", "a\u007Fb" },             // DEL
        { "x-echo", " a" },                   // leading SP
        { "x-echo", "a\t" },                  // trailing HTAB
        { "x echo", "value" },                // SP in the name
        { "x-echo:", "value" },               // ':' outside a pseudo-header
        { "x\0echo", "value" },               // NUL in the name
        { "x-echo\r\nx-injected", "1" },      // CR LF in the name
        { "x(echo)", "value" },               // a delimiter, not a tchar
    };

    /// <summary>Pseudo-header fields whose value breaks the rule.</summary>
    public static TheoryData<string, string> InvalidPseudoHeaderValues => new()
    {
        { ":authority", "api.test\r\nx-injected: 1" },
        { ":authority", "api.test\0" },
        { ":scheme", "https " },
        { ":method", "GET\u0001" },
    };

    // ------------------------------------------------------------------ HTTP/2

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/2 head field that breaks the rule should reset only its stream with PROTOCOL_ERROR")]
    [MemberData(nameof(InvalidFields))]
    public async Task Http2Receive_OnInvalidHeadField_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act — stream 1 carries the field; stream 3 is an ordinary sibling.
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/malformed").Append((name, value)).ToArray());
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        await ShouldResetHttp2StreamBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/2 pseudo-header value that breaks the rule should reset only its stream with PROTOCOL_ERROR")]
    [MemberData(nameof(InvalidPseudoHeaderValues))]
    public async Task Http2Receive_OnInvalidPseudoHeaderValue_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        (string Name, string Value)[] fields = Http2TestPeer.Get("/malformed")
            .Select(field => field.Name == name ? (name, value) : field)
            .ToArray();

        // Act
        await peer.SendHeadersAsync(1, endStream: true, fields);
        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();

        // Assert
        await ShouldResetHttp2StreamBeforeDispatchAsync(peer, next);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/2 trailer field that breaks the rule should reset its stream with PROTOCOL_ERROR")]
    [MemberData(nameof(InvalidFields))]
    public async Task Http2Receive_OnInvalidTrailerField_ShouldResetStreamWithProtocolError(string name, string value)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: false);
        IHttpContext upload = await peer.ReceiveContextAsync();

        // Act — a valid field precedes the offending one, so a section published field by field would
        // leave it behind.
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(
            1,
            HPackTestEncoder.Literal("x-checksum", "abc123"),
            HPackTestEncoder.Literal(name, value)));
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");

        // Assert — the request never reads as complete, none of the section is published, and the
        // connection keeps serving.
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        await ShouldFailToReadBodyAsync(upload.Request.Body);
        upload.Request.Trailers.Count.ShouldBe(0);

        await peer.SendHeadersAsync(3, endStream: true, Http2TestPeer.Get("/next"));
        IHttpContext next = await peer.ReceiveContextAsync();
        next.Request.Path.Value.ShouldBe("/next");
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/2 value with HTAB inside it should reach the application")]
    public async Task Http2Receive_OnValueWithInnerTab_ShouldDispatch()
    {
        // Arrange — HTAB between visible characters is the one control character a value may hold.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();

        // Act
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/ok").Append(("x-echo", "a\tb")).ToArray());
        IHttpContext context = await peer.ReceiveContextAsync();

        // Assert
        context.Request.Headers[new HttpHeaderKey("x-echo")].Value.ShouldBe("a\tb");
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    // ------------------------------------------------------------------ HTTP/3

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/3 head field that breaks the rule should reset only its stream with H3_MESSAGE_ERROR")]
    [MemberData(nameof(InvalidFields))]
    public async Task Http3Receive_OnInvalidHeadField_ShouldResetStreamWithMessageError(string name, string value)
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();

        // Act
        Connection malformed = await peer.OpenRequestStreamAsync();
        await malformed.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"), (":scheme", "https"), (":path", "/malformed"), (":authority", "a"), (name, value)));
        malformed.Output.Complete();

        // Assert
        await ShouldResetHttp3StreamBeforeDispatchAsync(peer, malformed);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/3 pseudo-header value that breaks the rule should reset only its stream with H3_MESSAGE_ERROR")]
    [MemberData(nameof(InvalidPseudoHeaderValues))]
    public async Task Http3Receive_OnInvalidPseudoHeaderValue_ShouldResetStreamWithMessageError(string name, string value)
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        (string Name, string Value)[] fields = new (string Name, string Value)[] { (":method", "GET"), (":scheme", "https"), (":path", "/malformed"), (":authority", "a") }
            .Select(field => field.Name == name ? (name, value) : field)
            .ToArray();

        // Act
        Connection malformed = await peer.OpenRequestStreamAsync();
        await malformed.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(fields));
        malformed.Output.Complete();

        // Assert
        await ShouldResetHttp3StreamBeforeDispatchAsync(peer, malformed);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/3 trailer field that breaks the rule should reset its stream with H3_MESSAGE_ERROR")]
    [MemberData(nameof(InvalidFields))]
    public async Task Http3Receive_OnInvalidTrailerField_ShouldResetStreamWithMessageError(string name, string value)
    {
        // Arrange — a valid field precedes the offending one, so a section published field by field would
        // leave it behind.
        byte[] payload = Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello")),
            HttpProtocolPayloadFactory.CreateHttp3HeadersFrame(("x-checksum", "abc123"), (name, value)));
        TestConnection stream = new(payload);
        IHttpContext context = await ReceiveHttp3Async(stream);

        // Act
        using StreamReader reader = new(context.Request.Body);
        await Should.ThrowAsync<IOException>(() => reader.ReadToEndAsync());

        // Assert — none of the section is published, as on HTTP/2.
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        context.Request.Trailers.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Received Field Syntax: An HTTP/3 value with HTAB inside it should reach the application")]
    public async Task Http3Receive_OnValueWithInnerTab_ShouldDispatch()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"), (":scheme", "https"), (":path", "/ok"), (":authority", "a"), ("x-echo", "a\tb")));

        // Act
        IHttpContext context = await ReceiveHttp3Async(stream);

        // Assert
        context.Request.Headers[new HttpHeaderKey("x-echo")].Value.ShouldBe("a\tb");
    }

    // ------------------------------------------------------------------ Helpers

    /// <summary>
    /// Asserts that stream 1 was reset with <c>PROTOCOL_ERROR</c> without reaching the application — the
    /// first exchange dispatched is stream 3's — and that the connection stayed open to serve it.
    /// </summary>
    private static async Task ShouldResetHttp2StreamBeforeDispatchAsync(Http2TestPeer peer, IHttpContext next)
    {
        next.Request.Path.Value.ShouldBe("/next");
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == 1),
            "the reset of stream 1");
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders);
        peer.Output.Frames.ShouldNotContain(frame => frame.IsGoAway);

        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);
    }

    /// <summary>
    /// Opens a sibling request after <paramref name="malformed"/>, then asserts the sibling is the first
    /// exchange dispatched, that <paramref name="malformed"/> alone was reset with <c>H3_MESSAGE_ERROR</c>,
    /// and that the connection is still open.
    /// </summary>
    private static async Task ShouldResetHttp3StreamBeforeDispatchAsync(Http3InMemoryPeer peer, Connection malformed)
    {
        Connection sibling = await peer.OpenRequestStreamAsync();
        await sibling.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/next", "https", "a"));
        sibling.Output.Complete();

        IHttpContext next = await peer.NextContextAsync();
        next.Request.Path.Value.ShouldBe("/next");
        await peer.ConnectionContext.SendAsync(next).AsTask().WaitAsync(_timeout);

        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(malformed));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.MessageError);
        peer.Server.ConnectionClosed.IsCancellationRequested.ShouldBeFalse();
    }

    private static async Task ShouldFailToReadBodyAsync(Stream body)
    {
        // The read fails with the malformed-trailers condition itself or — once the reset landed — as the
        // request abort; either way the handler never mistakes the request for a complete one.
        Exception? failure = null;

        try
        {
            using StreamReader reader = new(body);
            await reader.ReadToEndAsync().WaitAsync(_timeout);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            failure = exception;
        }

        failure.ShouldNotBeNull("reading the body of a request whose trailer section is malformed must fail");
    }

    private static async Task<IHttpContext> ReceiveHttp3Async(TestConnection stream)
    {
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(stream)), static _ => { });

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connection.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return enumerator.Current;
    }

    private static byte[] Combine(params byte[][] parts)
    {
        using MemoryStream buffer = new();

        foreach (byte[] part in parts)
        {
            buffer.Write(part, 0, part.Length);
        }

        return buffer.ToArray();
    }
}
