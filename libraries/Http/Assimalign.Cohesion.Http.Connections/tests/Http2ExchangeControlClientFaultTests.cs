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
/// The HTTP/2 exchange control's client-fault report (#1378). A body the transport rejects while the
/// application reads it — over the size cap (<c>413</c>), below the minimum data rate (<c>408</c>), or
/// malformed: content that contradicts its <c>content-length</c>, or a malformed trailer section, which
/// resets the stream with <c>PROTOCOL_ERROR</c> and is reported as <c>400</c> — fails the read with an
/// <see cref="IOException"/>, never the cancellation the reset fires, and
/// <see cref="IHttpExchangeControl.ClientFaultStatusCode"/> reports the status, so a host can tell the
/// client's fault from the application's.
/// </summary>
public class Http2ExchangeControlClientFaultTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A body over the size cap should be reported as a 413 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyOverCap_ShouldReportContentTooLarge()
    {
        // Arrange
        await using Http2TestPeer peer = await ConnectAsync(http2 => http2.Limits.MaxRequestBodySize = 16);
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);
        HttpStatusCode? beforeRead = control.ClientFaultStatusCode;

        // Act
        await peer.SendDataAsync(1, Http2TestPeer.CreateBody(32), endStream: false);
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // Assert
        beforeRead.ShouldBeNull();
        failure.ShouldBeOfType<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ReadStatusAsync(peer, streamId: 1)).ShouldBe("413");

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A body below the minimum data rate should be reported as a 408 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyBelowMinimumDataRate_ShouldReportRequestTimeout()
    {
        // Arrange — the head leaves the stream open for a body the peer never sends.
        await using Http2TestPeer peer = await ConnectAsync(
            http2 => http2.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(200)));
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);

        // Act
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // Assert
        failure.ShouldBeOfType<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.RequestTimeout);
        (await ReadStatusAsync(peer, streamId: 1)).ShouldBe("408");

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A malformed trailer section should be reported as a 400 client fault and fail every later read with the fault")]
    public async Task ClientFaultStatusCode_OnMalformedTrailerSection_ShouldReportBadRequest()
    {
        // Arrange — "content-length" is prohibited in a trailer section (RFC 9110 §6.5.1).
        await using Http2TestPeer peer = await ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: false);
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);

        // Act — the reset lands before the read, so the read meets the abort first.
        await peer.SendAsync(HPackTestEncoder.TrailersFrame(1, HPackTestEncoder.Literal("content-length", "5")));
        await WaitForResetAsync(peer, streamId: 1);
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // Assert — the client's fault, not the cancellation the reset also fired.
        exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        failure.ShouldBeOfType<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.BadRequest);
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A body that contradicts its content-length should be reported as a 400 client fault and record it on the exchange")]
    public async Task ClientFaultStatusCode_OnContentLengthViolation_ShouldReportBadRequestAndRecordIt()
    {
        // Arrange — the stream ends after 16 of the 100 octets the head declares.
        await using Http2TestPeer peer = await ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "100")));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);

        // Act
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"), endStream: true);
        await WaitForResetAsync(peer, streamId: 1);
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // A host's fault boundary stages a 500 for the failed read; the stream carries no response, so the
        // send writes nothing and the exchange records the client fault's 400 instead.
        exchange.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        failure.ShouldBeOfType<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.BadRequest);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        peer.Output.ForStream(1).ShouldNotContain(frame => frame.IsHeaders);
        peer.Output.ForStream(1).Single(frame => frame.IsRstStream).GetRstStreamErrorCode().ShouldBe(Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A well-formed body should report no client fault")]
    public async Task ClientFaultStatusCode_OnWellFormedBody_ShouldReportNull()
    {
        // Arrange
        await using Http2TestPeer peer = await ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload", ("content-length", "5")));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);

        // Act
        await peer.SendDataAsync(1, Encoding.ASCII.GetBytes("hello"), endStream: true);
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // Assert
        failure.ShouldBeNull();
        control.ClientFaultStatusCode.ShouldBeNull();

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 Client Fault: A peer reset should report no client fault and fail the read as cancelled")]
    public async Task ClientFaultStatusCode_OnPeerReset_ShouldReportNull()
    {
        // Arrange
        await using Http2TestPeer peer = await ConnectAsync();
        await peer.SendHeadersAsync(1, endStream: false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        IHttpExchangeControl control = GetControl(exchange);

        // Act — the client cancels its own request.
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await peer.SyncAsync();
        Exception? failure = await TryReadToEndAsync(exchange.Request.Body);

        // Assert — a cancellation, not a client fault.
        failure.ShouldBeAssignableTo<OperationCanceledException>();
        control.ClientFaultStatusCode.ShouldBeNull();

        await peer.ConnectionContext.SendAsync(exchange).AsTask().WaitAsync(_timeout);
    }

    private static Task<Http2TestPeer> ConnectAsync(Action<Http2ConnectionListenerOptions>? configure = null)
    {
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new ControlCapturingInterceptor());
        return Http2TestPeer.ConnectAsync(options, configure);
    }

    private static IHttpExchangeControl GetControl(IHttpContext exchange)
    {
        return exchange.Features.Get<CapturedControlFeature>().ShouldNotBeNull().Control;
    }

    private static async Task WaitForResetAsync(Http2TestPeer peer, int streamId)
    {
        await peer.Output.ReadUntilAsync(
            frames => frames.Any(frame => frame.IsRstStream && frame.StreamId == streamId),
            $"the reset of stream {streamId}");
    }

    private static async Task<string> ReadStatusAsync(Http2TestPeer peer, int streamId)
    {
        IReadOnlyList<Http2WireFrame> frames = await peer.Output.ReadUntilAsync(
            observed => observed.Any(frame => frame.IsHeaders && frame.StreamId == streamId),
            $"the response on stream {streamId}");
        Http2WireFrame head = frames.Single(frame => frame.IsHeaders && frame.StreamId == streamId);
        return HttpProtocolPayloadFactory.DecodeLiteralHttp2Headers(head.Payload)[":status"];
    }

    private static async Task<Exception?> TryReadToEndAsync(Stream body)
    {
        try
        {
            byte[] buffer = new byte[256];
            while (await body.ReadAsync(buffer).AsTask().WaitAsync(_timeout) > 0)
            {
            }

            return null;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
    }

    /// <summary>
    /// Takes every exchange into the response phase and publishes its control as a feature.
    /// </summary>
    private sealed class ControlCapturingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Response;

        public override void BeforeResponse(HttpExchangeInterceptorResponseContext context)
        {
            context.Features.Set(new CapturedControlFeature(context.Control.ShouldNotBeNull()));
        }
    }

    private sealed class CapturedControlFeature : IHttpFeature
    {
        public CapturedControlFeature(IHttpExchangeControl control)
        {
            Control = control;
        }

        public string Name => nameof(CapturedControlFeature);

        public IHttpExchangeControl Control { get; }
    }
}
