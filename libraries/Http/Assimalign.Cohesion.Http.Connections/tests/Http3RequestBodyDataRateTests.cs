using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Verifies the HTTP/3 minimum request-body data rate (#1085): a body the peer holds back past the grace
/// period fails the read, the request stream is stopped with <c>STOP_SENDING(H3_NO_ERROR)</c>
/// (RFC 9114 §4.1), and the exchange is answered <c>408</c> (RFC 9110 §15.5.9) while its response head is
/// uncommitted. A peer that keeps the rate is never rejected, and a CONNECT tunnel may idle. A <c>408</c>
/// head that a <c>BeforeResponseHead</c> hook leaves unsendable is refused like any other (#1183): nothing
/// reaches the wire, and the exchange keeps the connection busy until its replacement is sent.
/// </summary>
public class Http3RequestBodyDataRateTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _keepAlive = TimeSpan.FromMilliseconds(300);
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(300));
    private static readonly HttpHeaderKey _injectedField = new("x-injected");

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: A body held back should fail the read, stop the stream with H3_NO_ERROR, and be answered 408")]
    public async Task ReadBody_OnBodyBelowRate_ShouldStopWithNoErrorAndAnswer408()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();

        // Act — the application reads the body; the peer sends none of it.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));

        // Assert — the peer is asked to stop sending, with the code that promises a complete response
        // (it reaches the peer's writer on its next flush) …
        ConnectionResetException stop = await Should.ThrowAsync<ConnectionResetException>(() => request.Output.WriteAsync(new byte[1]).AsTask());
        stop.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.NoError);

        // … and that response is a 408, whatever the application staged.
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        frames.Count.ShouldBe(1);
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"].ShouldBe("408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: A 408 head a BeforeResponseHead hook makes unsendable should be refused, keep the connection open, and be replaced by the 408")]
    public async Task SendAsync_OnBodyBelowRateWithHookRefusedHead_ShouldKeepConnectionOpenAndAnswer408()
    {
        // Arrange — the peer holds the body back, and a response interceptor's BeforeResponseHead hook adds
        // a value no head may carry. HTTP/3 replaces the staged response with its 408 before it runs the
        // hooks, so the hook's field lands in the 408's own head: only HTTP/3 can refuse that head.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            http3 =>
            {
                http3.Limits.MinRequestBodyDataRate = _rate;
                http3.Limits.KeepAliveTimeout = _keepAlive;
                http3.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            },
            static options => options.Interceptors.Add(new FieldInjectingInterceptor()));
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        Task<bool> next = peer.MoveNextAsync();
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[16]).AsTask().WaitAsync(_timeout));
        context.Response.StatusCode = HttpStatusCode.InternalServerError;

        // Act — the 408's head is refused, and the replacement is not sent for three keep-alive periods.
        HttpException refusal = await Should.ThrowAsync<HttpException>(() => peer.ConnectionContext.SendAsync(context).AsTask());
        await Task.Delay(_keepAlive * 3);

        // Assert — nothing reached the wire, and the refused exchange still runs, so the connection is
        // busy and not closed.
        refusal.Code.ShouldBe(HttpErrorCode.InvalidResponseField);
        context.HasResponseStarted.ShouldBeFalse();
        request.Input.TryRead(out _).ShouldBeFalse();
        next.IsCompleted.ShouldBeFalse();

        // The host's replacement is answered with the transport's 408, the only frame on the stream. The
        // hooks ran once, so the field is not added again; the exchange then ends, and the idle
        // connection is closed after the keep-alive deadline.
        context.Response.Headers.Clear();
        context.Response.Body = new MemoryStream();
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));
        frames.Count.ShouldBe(1);
        Dictionary<string, string> head = HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload);
        head[":status"].ShouldBe("408");
        head.ShouldNotContainKey("x-injected");
        (await next).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: A body that keeps the rate should be read in full")]
    public async Task ReadBody_OnBodyAboveRate_ShouldDeliverWholeBody()
    {
        // Arrange — 50 octets every 100 ms is 500 octets per second, five times the rate, for longer than
        // the grace period.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        byte[] body = new byte[500];
        Random.Shared.NextBytes(body);
        Task<byte[]> received = ReadToEndAsync(context.Request.Body);

        // Act
        for (int offset = 0; offset < body.Length; offset += 50)
        {
            await Task.Delay(100);
            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, body[offset..(offset + 50)]));
        }

        request.Output.Complete();

        // Assert
        (await received.WaitAsync(_timeout)).ShouldBe(body);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Data Rate: An idle extended CONNECT tunnel should not be held to the rate")]
    public async Task ReadBody_OnIdleExtendedConnectTunnel_ShouldNotApplyRate()
    {
        // Arrange — RFC 9110 §9.3.6: a CONNECT's DATA is tunnel traffic, which may idle indefinitely.
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(http3 => http3.Limits.MinRequestBodyDataRate = _rate);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "CONNECT"),
            (":protocol", "websocket"),
            (":scheme", "https"),
            (":path", "/chat"),
            (":authority", "api.test")));
        IHttpContext context = await peer.NextContextAsync();

        // Act — the tunnel idles for twice the grace period.
        Task<int> read = context.Request.Body.ReadAsync(new byte[16]).AsTask();
        await Task.Delay(_rate.GracePeriod * 2);

        // Assert — nothing was rejected, and the tunnel still carries the peer's octets.
        read.IsCompleted.ShouldBeFalse();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("ping")));
        (await read.WaitAsync(_timeout)).ShouldBe(4);
    }

    private static async Task<byte[]> ReadToEndAsync(Stream body)
    {
        using MemoryStream received = new();
        await body.CopyToAsync(received);
        return received.ToArray();
    }

    /// <summary>
    /// A response interceptor whose <c>BeforeResponseHead</c> hook adds a field value carrying CR LF, which
    /// every head encoder refuses (#1183, CWE-113).
    /// </summary>
    private sealed class FieldInjectingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override ValueTask BeforeResponseHeadAsync(HttpExchangeInterceptorResponseContext context, CancellationToken cancellationToken)
        {
            context.Headers[_injectedField] = "a\r\nLocation: /evil";
            return ValueTask.CompletedTask;
        }
    }
}
