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
/// Every response head writer refuses a field it cannot carry before it writes a byte (#1183, decision
/// 27, CWE-113): a name that is not a token, or a value holding CR, LF, NUL, or another control character
/// but HTAB (RFC 9110 §5.1, §5.5). The refusal is an <see cref="HttpException"/> carrying
/// <see cref="HttpErrorCode.InvalidResponseField"/>. Nothing of the head reaches the wire and the
/// response has not started, so the exchange can still be answered — each test then sends a replacement
/// and shows it is the first thing on the wire. A streamed response whose trailer section is refused
/// already has its head and body out, so its stream is reset instead.
/// </summary>
public class HttpResponseFieldSyntaxTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpHeaderKey _field = new("x-echo");

    /// <summary>Values a field line cannot carry: line breaks, NUL, and the other control characters.</summary>
    public static TheoryData<string> InvalidValues => new()
    {
        "a\r\nSet-Cookie: injected=1",
        "a\nSet-Cookie: injected=1",
        "a\rb",
        "a\0b",
        "a\u0001b",
        "a\u007Fb",
    };

    /// <summary>Names that are not tokens.</summary>
    public static TheoryData<string> InvalidNames => new()
    {
        "x-echo\r\nSet-Cookie",
        "x echo",
        "x-echo:",
        "x\0echo",
    };

    // ------------------------------------------------------------------ HTTP/1.1

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 head with a value it cannot carry should be refused before a byte is written")]
    [MemberData(nameof(InvalidValues))]
    public async Task Http1SendAsync_OnInvalidFieldValue_ShouldRefuseBeforeWriting(string value)
    {
        // Arrange
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync();
        exchange.Context.Response.Headers[_field] = value;
        exchange.Context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("body"));

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => exchange.Connection.SendAsync(exchange.Context).AsTask());

        // Assert — refused, and unstarted: a replacement is the first thing on the wire.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        exchange.Context.HasResponseStarted.ShouldBeFalse();
        string wire = await exchange.SendReplacementAsync();
        wire.ShouldStartWith("HTTP/1.1 500");
        wire.ShouldNotContain("injected");
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 head with a name that is not a token should be refused before a byte is written")]
    [MemberData(nameof(InvalidNames))]
    public async Task Http1SendAsync_OnInvalidFieldName_ShouldRefuseBeforeWriting(string name)
    {
        // Arrange
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync();
        exchange.Context.Response.Headers[new HttpHeaderKey(name)] = "value";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => exchange.Connection.SendAsync(exchange.Context).AsTask());

        // Assert — the message never quotes the name, which may hold CR, LF, or NUL.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        refusal.Message.ShouldNotContain(name);
        (await exchange.SendReplacementAsync()).ShouldStartWith("HTTP/1.1 500");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 Set-Cookie value it cannot carry should be refused before a byte is written")]
    public async Task Http1SendAsync_OnInvalidSetCookieValue_ShouldRefuseBeforeWriting()
    {
        // Arrange — Set-Cookie goes out one line per value, so each value is checked on its own.
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync();
        exchange.Context.Response.Headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[] { "a=1", "b=2\r\nLocation: /evil" });

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => exchange.Connection.SendAsync(exchange.Context).AsTask());

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        string wire = await exchange.SendReplacementAsync();
        wire.ShouldStartWith("HTTP/1.1 500");
        wire.ShouldNotContain("evil");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 value with HTAB inside it should still be sent")]
    public async Task Http1SendAsync_OnValueWithInnerTab_ShouldSend()
    {
        // Arrange — HTAB is the one control character a field value may hold (RFC 9110 §5.5).
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync();
        exchange.Context.Response.Headers[_field] = "a\tb";

        // Act
        await exchange.Connection.SendAsync(exchange.Context).AsTask().WaitAsync(_timeout);

        // Assert
        (await exchange.ReadWireUntilAsync("\r\n\r\n")).ShouldContain("x-echo: a\tb\r\n");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 early hint with a value it cannot carry should be refused before a byte is written")]
    public async Task Http1EarlyHints_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync(static options => options.Interceptors.Add(HttpInterimResponses.CreateInterceptor()));
        HttpHeaderCollection hints = new();
        hints[HttpHeaderKey.Link] = "</a.css>; rel=preload\r\nSet-Cookie: injected=1";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => exchange.Context.Features.Get<IHttpInterimResponseFeature>()!
            .SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints)
            .AsTask());

        // Assert — no interim head went out: the final response is the first thing on the wire.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        exchange.Context.Response.StatusCode = HttpStatusCode.Ok;
        await exchange.Connection.SendAsync(exchange.Context).AsTask().WaitAsync(_timeout);
        (await exchange.ReadWireUntilAsync("\r\n\r\n")).ShouldStartWith("HTTP/1.1 200");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/1.1 streamed head it cannot carry should be refused before a byte is written and leave no chunked coding behind")]
    public async Task Http1StreamingWrite_OnInvalidFieldValue_ShouldRefuseAndWithdrawChunkedCoding()
    {
        // Arrange
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync(static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        exchange.Context.Response.Headers[_field] = "a\r\nSet-Cookie: injected=1";

        // Act — the first write commits the head, so it is the write that fails.
        HttpException refusal = await Should.ThrowAsync<HttpException>(
            () => exchange.Context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk")).AsTask());

        // Assert — unstarted, and the Transfer-Encoding chosen for the refused head is gone, so the
        // buffered replacement is framed by its Content-Length alone.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        exchange.Context.HasResponseStarted.ShouldBeFalse();
        exchange.Context.Response.Headers.ContainsKey(HttpHeaderKey.TransferEncoding).ShouldBeFalse();
        string wire = await exchange.SendReplacementAsync();
        wire.ShouldStartWith("HTTP/1.1 500");
        wire.ShouldNotContain("Transfer-Encoding");
        wire.ShouldContain("Content-Length: 0");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: A refused HTTP/1.1 buffered head should leave no synthesized Content-Length to misframe its replacement")]
    public async Task Http1SendAsync_OnRefusedBufferedHead_ShouldFrameReplacementByItsOwnBody()
    {
        // Arrange — an 11-octet body, so the refused head synthesized Content-Length: 11.
        await using Http1Exchange exchange = await Http1Exchange.OpenAsync();
        exchange.Context.Response.Headers[HttpHeaderKey.Location] = "/next\r\nSet-Cookie: injected=1";
        exchange.Context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("eleven-byte"));
        (await Should.ThrowAsync<HttpException>(() => exchange.Connection.SendAsync(exchange.Context).AsTask()))
            .Code.ShouldBe(HttpErrorCode.InvalidResponseField);

        // Act — a host that removes only the offending field and swaps in a shorter body.
        exchange.Context.Response.Headers.Remove(HttpHeaderKey.Location);
        exchange.Context.Response.StatusCode = HttpStatusCode.InternalServerError;
        exchange.Context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("oops"));
        await exchange.Connection.SendAsync(exchange.Context).AsTask().WaitAsync(_timeout);

        // Assert — framed by the body it carries, so the connection stays aligned for the next response.
        string wire = await exchange.ReadWireUntilAsync("\r\n\r\noops");
        wire.ShouldStartWith("HTTP/1.1 500");
        wire.ShouldContain("Content-Length: 4\r\n");
        wire.ShouldNotContain("Content-Length: 11");
    }

    // ------------------------------------------------------------------ HTTP/2

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 head with a value it cannot carry should be refused before a frame is written")]
    [MemberData(nameof(InvalidValues))]
    public async Task Http2SendAsync_OnInvalidFieldValue_ShouldRefuseBeforeWriting(string value)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Headers[_field] = value;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("body"));

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await peer.SyncAsync();

        // Assert — nothing on stream 1, and the stream still takes a response.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 head with a name that is not a token should be refused before a frame is written")]
    [MemberData(nameof(InvalidNames))]
    public async Task Http2SendAsync_OnInvalidFieldName_ShouldRefuseBeforeWriting(string name)
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Headers[new HttpHeaderKey(name)] = "value";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await peer.SyncAsync();

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 Set-Cookie value it cannot carry should be refused before a frame is written")]
    public async Task Http2SendAsync_OnInvalidSetCookieValue_ShouldRefuseBeforeWriting()
    {
        // Arrange — Set-Cookie goes out one field line per value, through its own branch of the encoder,
        // so each value is checked on its own.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[] { "a=1", "b=2\r\nLocation: /evil" });

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await peer.SyncAsync();

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 value with whitespace at its ends should be sent without it")]
    [InlineData("header")]
    [InlineData("set-cookie")]
    [InlineData("trailer")]
    public async Task Http2SendAsync_OnValueWithEdgeWhitespace_ShouldSendItTrimmed(string section)
    {
        // Arrange — RFC 9113 §8.2.1 makes the value malformed as written; RFC 9110 §5.5 reads it as "a\tb=1".
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        string name = StageValueWithEdgeWhitespace(context, section);

        // Act
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — the trailer section is the last HEADERS frame, the head the first.
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        Http2WireFrame fields = section == "trailer" ? frames.Last(frame => frame.IsHeaders) : frames[0];
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(fields.Payload)[name].ShouldBe("a\tb=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 connection-specific field it cannot carry should be refused although it is never sent")]
    public async Task Http2SendAsync_OnInvalidConnectionSpecificField_ShouldRefuse()
    {
        // Arrange — HTTP/2 drops Connection from the head, but HTTP/1.1 would write it, so the field is
        // refused on every version alike.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Headers[HttpHeaderKey.Connection] = "close\r\nSet-Cookie: injected=1";

        // Act and Assert
        (await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask()))
            .Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 buffered trailer value it cannot carry should be refused before a frame is written")]
    public async Task Http2SendAsync_OnInvalidTrailerValue_ShouldRefuseBeforeWriting()
    {
        // Arrange — the value passed the staging check, then its array changed underneath it.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        StageTrailerThenCorrupt(context);

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await peer.SyncAsync();

        // Assert — not even the head went out: the head and the trailers are encoded together first.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: A refused HTTP/2 buffered response should leave no synthesized content-length to misframe its replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http2SendAsync_OnRefusedBufferedResponse_ShouldFrameReplacementByItsOwnBody(bool refuseTrailer)
    {
        // Arrange — an 11-octet body; the head or the trailer section is refused.
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/echo"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("eleven-byte"));
        RefuseHeadOrTrailer(context, refuseTrailer);
        (await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask()))
            .Code.ShouldBe(HttpErrorCode.InvalidResponseField);

        // Act — a host that removes only the offending field and swaps in a shorter body.
        RemoveRefusedField(context, refuseTrailer);
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("oops"));
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert — a stale content-length: 11 would make the response malformed (RFC 9113 §8.1.1).
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(frames[0].Payload)["content-length"].ShouldBe("4");
        frames.Where(frame => frame.IsData).Sum(frame => frame.Payload.Length).ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 streamed head it cannot carry should be refused before a frame is written")]
    public async Task Http2StreamingWrite_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.Headers[_field] = "a\r\nb";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(
            () => context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk")).AsTask());
        await peer.SyncAsync();

        // Assert — the stream was not claimed, so the buffered replacement goes out on it.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 streamed trailer value it cannot carry should reset the stream with INTERNAL_ERROR")]
    public async Task Http2SendAsync_OnInvalidTrailerAfterStreamedBody_ShouldResetWithInternalError()
    {
        // Arrange — the head and the body are out before the trailers are known.
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/events"));
        IHttpContext context = await peer.ReceiveContextAsync();
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
        await context.Response.Streaming.FlushAsync();
        StageTrailerThenCorrupt(context);

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await peer.SyncAsync();

        // Assert — the response cannot end as if it were whole: RST_STREAM(INTERNAL_ERROR), no END_STREAM.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        IReadOnlyList<Http2WireFrame> frames = peer.Output.ForStream(1);
        frames.ShouldNotContain(frame => frame.EndStream);
        frames[^1].IsRstStream.ShouldBeTrue();
        frames[^1].GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.InternalError);
        ((Http2ConnectionContext)peer.ConnectionContext).StreamCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 early hint with a value it cannot carry should be refused before a frame is written")]
    public async Task Http2EarlyHints_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpInterimResponses.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/page"));
        IHttpContext context = await peer.ReceiveContextAsync();
        HttpHeaderCollection hints = new();
        hints[HttpHeaderKey.Link] = "</a.css>; rel=preload\nx: y";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => context.Features.Get<IHttpInterimResponseFeature>()!
            .SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints)
            .AsTask());
        await peer.SyncAsync();

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        peer.Output.ForStream(1).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/2 tunnel head with a value it cannot carry should be refused before a frame is written")]
    public async Task Http2AcceptTunnel_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor());
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(options);
        await peer.SendHeadersAsync(1, endStream: false, (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/chat"), (":authority", "api.test"));
        IHttpContext context = await peer.ReceiveContextAsync();
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        context.Response.Headers[_field] = "a\r\nb";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());
        await peer.SyncAsync();

        // Assert — unclaimed and unstarted, with the status the application staged.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        peer.Output.ForStream(1).ShouldBeEmpty();
        await ShouldAnswerHttp2ReplacementAsync(peer, context);
    }

    // ------------------------------------------------------------------ HTTP/3

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 head with a value it cannot carry should be refused before a frame is written")]
    [MemberData(nameof(InvalidValues))]
    public async Task Http3SendAsync_OnInvalidFieldValue_ShouldRefuseBeforeWriting(string value)
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/echo", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Headers[_field] = value;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("body"));

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask());

        // Assert — the replacement's HEADERS frame is the first frame on the stream.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 head with a name that is not a token should be refused before a frame is written")]
    [MemberData(nameof(InvalidNames))]
    public async Task Http3SendAsync_OnInvalidFieldName_ShouldRefuseBeforeWriting(string name)
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/echo", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Headers[new HttpHeaderKey(name)] = "value";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask());

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 Set-Cookie value it cannot carry should be refused before a frame is written")]
    public async Task Http3SendAsync_OnInvalidSetCookieValue_ShouldRefuseBeforeWriting()
    {
        // Arrange — Set-Cookie goes out one field line per value, through its own branch of the encoder,
        // so each value is checked on its own.
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/echo", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Headers[HttpHeaderKey.SetCookie] = new HttpHeaderValue(new[] { "a=1", "b=2\r\nLocation: /evil" });

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask());

        // Assert — the replacement's HEADERS frame is the first frame on the stream.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 value with whitespace at its ends should be sent without it")]
    [InlineData("header")]
    [InlineData("set-cookie")]
    [InlineData("trailer")]
    public async Task Http3SendAsync_OnValueWithEdgeWhitespace_ShouldSendItTrimmed(string section)
    {
        // Arrange — RFC 9114 §10.3 makes the value malformed as written; RFC 9110 §5.5 reads it as "a\tb=1".
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/echo", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        string name = StageValueWithEdgeWhitespace(context, section);

        // Act
        await connection.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — the trailer section is the last HEADERS frame, the head the first.
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        byte[] fields = section == "trailer"
            ? frames.Last(frame => frame.FrameType == (long)Http3FrameType.Headers).Payload
            : frames[0].Payload;
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(fields)[name].ShouldBe("a\tb=1");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 buffered trailer value it cannot carry should be refused before a frame is written")]
    public async Task Http3SendAsync_OnInvalidTrailerValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/report", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("hello"));
        StageTrailerThenCorrupt(context);

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask());

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: A refused HTTP/3 buffered response should leave no synthesized content-length to misframe its replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http3SendAsync_OnRefusedBufferedResponse_ShouldFrameReplacementByItsOwnBody(bool refuseTrailer)
    {
        // Arrange — an 11-octet body; the head or the trailer section is refused.
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/echo", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(stream);
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("eleven-byte"));
        RefuseHeadOrTrailer(context, refuseTrailer);
        (await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask()))
            .Code.ShouldBe(HttpErrorCode.InvalidResponseField);

        // Act — a host that removes only the offending field and swaps in a shorter body.
        RemoveRefusedField(context, refuseTrailer);
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        context.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes("oops"));
        await connection.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert — a stale content-length: 11 would make the response malformed (RFC 9114 §4.1.2).
        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)["content-length"].ShouldBe("4");
        frames.Where(frame => frame.FrameType == (long)Http3FrameType.Data).Sum(frame => frame.Payload.Length).ShouldBe(4);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 streamed head it cannot carry should be refused before a frame is written")]
    public async Task Http3StreamingWrite_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/events", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(
            stream,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        context.Response.Headers[_field] = "a\0b";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(
            () => context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk")).AsTask());

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 streamed trailer value it cannot carry should reset the stream with H3_INTERNAL_ERROR")]
    public async Task Http3SendAsync_OnInvalidTrailerAfterStreamedBody_ShouldResetWithInternalError()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/events", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(
            stream,
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()));
        await context.Response.Streaming.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
        await context.Response.Streaming.FlushAsync();
        StageTrailerThenCorrupt(context);

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => connection.SendAsync(context).AsTask());

        // Assert
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        stream.IsAborted.ShouldBeTrue();
        stream.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.InternalError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 early hint with a value it cannot carry should be refused before a frame is written")]
    public async Task Http3EarlyHints_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        TestConnection stream = new(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/page", "https", "a"));
        (IHttpConnectionContext connection, IHttpContext context) = await ReceiveHttp3Async(
            stream,
            static options => options.Interceptors.Add(HttpInterimResponses.CreateInterceptor()));
        HttpHeaderCollection hints = new();
        hints[HttpHeaderKey.Link] = "</a.css>; rel=preload\rx";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => context.Features.Get<IHttpInterimResponseFeature>()!
            .SendInterimResponseAsync(HttpStatusCode.EarlyHints, hints)
            .AsTask());

        // Assert — no interim HEADERS frame: the final response's is the first.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        await ShouldAnswerHttp3ReplacementAsync(stream, connection, context);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: An HTTP/3 tunnel head with a value it cannot carry should be refused before a frame is written")]
    public async Task Http3AcceptTunnel_OnInvalidFieldValue_ShouldRefuseBeforeWriting()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configureListener: static options => options.Interceptors.Add(HttpExtendedConnect.CreateInterceptor()));
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"), (":protocol", "websocket"), (":scheme", "https"), (":path", "/chat"), (":authority", "api.test")));
        IHttpContext context = await peer.NextContextAsync();
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        context.Response.Headers[_field] = "a\r\nb";

        // Act
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => context.ExtendedConnect!.AcceptAsync().AsTask());

        // Assert — unstarted, with the status the application staged; the replacement is the first frame.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        PrepareReplacement(context);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("500");
    }

    // ------------------------------------------------------------------ Trailer staging

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Response Field Syntax: A trailer value it cannot carry should be refused when staged")]
    public async Task Trailers_OnInvalidValue_ShouldThrowWhenAdded()
    {
        // Arrange
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: true, Http2TestPeer.Get("/report"));
        IHttpContext context = await peer.ReceiveContextAsync();
        IHttpTrailerCollection trailers = context.Response.Trailers;

        // Act and Assert — through Add and through the indexer, for the value and for the name.
        Should.Throw<ArgumentException>(() => trailers.Add(new HttpHeaderKey("x-checksum"), "abc\r\nx: y"));
        Should.Throw<ArgumentException>(() => trailers[new HttpHeaderKey("x-checksum")] = "abc\0");
        Should.Throw<ArgumentException>(() => trailers[new HttpHeaderKey("x checksum")] = "abc");
        trailers.Count.ShouldBe(0);

        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    // ------------------------------------------------------------------ Helpers

    // A value built over an array shares it with its caller, so it can change after the staging check.
    private static void StageTrailerThenCorrupt(IHttpContext context)
    {
        string?[] values = ["abc123"];
        context.Response.Trailers[new HttpHeaderKey("x-checksum")] = new HttpHeaderValue(values);
        values[0] = "abc123\r\nx-injected: 1";
    }

    // A value with SP and HTAB at its ends, which HTTP/2 and HTTP/3 cannot carry, staged in one section.
    private static string StageValueWithEdgeWhitespace(IHttpContext context, string section)
    {
        const string value = " a\tb=1 \t";

        switch (section)
        {
            case "set-cookie":
                context.Response.Headers[HttpHeaderKey.SetCookie] = value;
                return "set-cookie";
            case "trailer":
                context.Response.Trailers[new HttpHeaderKey("x-checksum")] = value;
                return "x-checksum";
            default:
                context.Response.Headers[_field] = value;
                return _field.Value;
        }
    }

    // A response the transport refuses for one field: a reflected Location in the head, or a trailer.
    private static void RefuseHeadOrTrailer(IHttpContext context, bool refuseTrailer)
    {
        if (refuseTrailer)
        {
            StageTrailerThenCorrupt(context);
        }
        else
        {
            context.Response.Headers[HttpHeaderKey.Location] = "/next\r\nx-injected: 1";
        }
    }

    // What a host that removes only the offending field does; everything else it leaves as it was.
    private static void RemoveRefusedField(IHttpContext context, bool refuseTrailer)
    {
        if (refuseTrailer)
        {
            context.Response.Trailers.Remove(new HttpHeaderKey("x-checksum"));
        }
        else
        {
            context.Response.Headers.Remove(HttpHeaderKey.Location);
        }
    }

    // What a host does with a refused response: a bare 500 in its place.
    private static void PrepareReplacement(IHttpContext context)
    {
        context.Response.Headers.Clear();

        if (context.Response.Trailers.IsSupported)
        {
            context.Response.Trailers.Clear();
        }

        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
    }

    private static async Task ShouldAnswerHttp2ReplacementAsync(Http2TestPeer peer, IHttpContext context)
    {
        PrepareReplacement(context);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        Http2WireFrame head = peer.Output.ForStream(1).First();
        head.IsHeaders.ShouldBeTrue();
        HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)[":status"].ShouldBe("500");
    }

    private static async Task ShouldAnswerHttp3ReplacementAsync(TestConnection stream, IHttpConnectionContext connection, IHttpContext context)
    {
        PrepareReplacement(context);
        await connection.SendAsync(context).AsTask().WaitAsync(_timeout);

        IReadOnlyList<(long FrameType, byte[] Payload)> frames = HttpProtocolPayloadFactory.ParseHttp3Frames(await stream.ReadOutputAsync());
        frames[0].FrameType.ShouldBe((long)Http3FrameType.Headers);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("500");
    }

    private static async Task<(IHttpConnectionContext Connection, IHttpContext Context)> ReceiveHttp3Async(
        TestConnection stream,
        Action<HttpConnectionListenerOptions>? configureListener = null)
    {
        HttpConnectionListenerOptions options = new();
        configureListener?.Invoke(options);
        options.UseHttp3(new TestMultiplexedConnectionListener(new TestMultiplexedConnection(stream)), static _ => { });

        HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();

        await using IAsyncEnumerator<IHttpContext> enumerator = connection.ReceiveAsync().GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        return (connection, enumerator.Current);
    }

    /// <summary>One HTTP/1.1 exchange on an in-memory connection, and the wire it writes.</summary>
    private sealed class Http1Exchange : IAsyncDisposable
    {
        private readonly HttpConnectionListener _listener;
        private readonly IAsyncEnumerator<IHttpContext> _exchanges;
        private readonly TestConnection _transport;
        private readonly StringBuilder _wire = new();

        private Http1Exchange(HttpConnectionListener listener, TestConnection transport, IHttpConnectionContext connection, IAsyncEnumerator<IHttpContext> exchanges)
        {
            _listener = listener;
            _transport = transport;
            Connection = connection;
            _exchanges = exchanges;
        }

        public IHttpConnectionContext Connection { get; }

        public IHttpContext Context => _exchanges.Current;

        public static async Task<Http1Exchange> OpenAsync(Action<HttpConnectionListenerOptions>? configure = null)
        {
            TestConnection transport = new(HttpProtocolPayloadFactory.CreateHttp1Request("GET /echo HTTP/1.1\r\nHost: api.test\r\n\r\n"));
            HttpConnectionListenerOptions options = new();
            configure?.Invoke(options);
            options.UseHttp1(new TestConnectionListener(transport));

            HttpConnectionListener listener = new(options);
            IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync()).OpenAsync();
            IAsyncEnumerator<IHttpContext> exchanges = connection.ReceiveAsync().GetAsyncEnumerator();
            (await exchanges.MoveNextAsync()).ShouldBeTrue();

            return new Http1Exchange(listener, transport, connection, exchanges);
        }

        // Sends the host's replacement and returns its head as the wire shows it.
        public async Task<string> SendReplacementAsync()
        {
            PrepareReplacement(Context);
            await Connection.SendAsync(Context).AsTask().WaitAsync(_timeout);
            return await ReadWireUntilAsync("\r\n\r\n");
        }

        public async Task<string> ReadWireUntilAsync(string marker)
        {
            while (!_wire.ToString().Contains(marker, StringComparison.Ordinal))
            {
                _wire.Append(Encoding.Latin1.GetString(await _transport.ReadOutputAsync().WaitAsync(_timeout)));
            }

            return _wire.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await _exchanges.DisposeAsync();
            await _listener.DisposeAsync();
        }
    }
}
