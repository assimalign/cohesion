using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// The HTTP/3 exchange control's client-fault report (#1378). A body the transport rejects while the
/// application reads it — over the size cap (<c>413</c>), below the minimum data rate (<c>408</c>), or
/// malformed: content that contradicts its Content-Length, or a malformed trailer section, which resets the
/// request stream with <c>H3_MESSAGE_ERROR</c> and is reported as <c>400</c>, or an invalid frame sequence,
/// which closes the connection and is reported as <c>400</c> too — fails the read with an
/// <see cref="IOException"/>, and <see cref="IHttpExchangeControl.ClientFaultStatusCode"/> reports the
/// status, so a host can tell the client's fault from the application's. A client that resets its own
/// request is not at fault: the read fails with the exchange's cancellation, as on HTTP/2.
/// </summary>
public class Http3ExchangeControlClientFaultTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A body over the size cap should be reported as a 413 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyOverCap_ShouldReportContentTooLarge()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await StartAsync(http3 => http3.Limits.MaxRequestBodySize = 16);
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, new byte[32])));
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        HttpStatusCode? beforeRead = control.ClientFaultStatusCode;

        // Act
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        beforeRead.ShouldBeNull();
        failure.ShouldBeAssignableTo<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await ReadStatusAsync(request)).ShouldBe("413");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A body below the minimum data rate should be reported as a 408 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyBelowMinimumDataRate_ShouldReportRequestTimeout()
    {
        // Arrange — the head leaves the stream open for a body the peer never sends.
        await using Http3InMemoryPeer peer = await StartAsync(
            http3 => http3.Limits.MinRequestBodyDataRate = new HttpMinDataRate(bytesPerSecond: 100, gracePeriod: TimeSpan.FromMilliseconds(200)));
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"));
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);

        // Act
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        failure.ShouldBeAssignableTo<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.RequestTimeout);
        (await ReadStatusAsync(request)).ShouldBe("408");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A body that contradicts its Content-Length should be reported as a 400 client fault and record it on the exchange")]
    public async Task ClientFaultStatusCode_OnContentLengthViolation_ShouldReportBadRequestAndRecordIt()
    {
        // Arrange — the stream ends after 16 of the 100 octets the head declares.
        await using Http3InMemoryPeer peer = await StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request(
                "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "100" }),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}"))));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);

        // Act — a host's fault boundary stages a 500 for the failed read; the stream carries no response,
        // so the send writes nothing and the exchange records the client fault's 400 instead.
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        failure.ShouldBeAssignableTo<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.MessageError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A malformed trailer section should be reported as a 400 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnMalformedTrailerSection_ShouldReportBadRequest()
    {
        // Arrange — "content-length" is prohibited in a trailer section (RFC 9110 §6.5.1).
        await using Http3InMemoryPeer peer = await StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}")),
            HttpProtocolPayloadFactory.CreateHttp3HeadersFrame(("content-length", "5"))));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);

        // Act
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        failure.ShouldBeAssignableTo<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(() => Http3InMemoryPeer.ReadToEndAsync(request));
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.MessageError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: DATA after the trailer section should be reported as a 400 client fault and record it on the exchange")]
    public async Task ClientFaultStatusCode_OnDataAfterTrailerSection_ShouldReportBadRequestAndRecordIt()
    {
        // Arrange — RFC 9114 §4.1: nothing but the stream's end may follow a trailer section, so the DATA
        // frame behind it is an invalid frame sequence, a connection error.
        byte[] json = Encoding.ASCII.GetBytes("{\"name\":\"gizmo\"}");
        await using Http3InMemoryPeer peer = await StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, json),
            HttpProtocolPayloadFactory.CreateHttp3HeadersFrame(("x-checksum", "abc")),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, json)));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);

        // Act — a host's fault boundary stages a 500 for the failed read; the connection is gone, so the
        // send writes nothing and the exchange records the client fault's 400 instead.
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        context.Response.StatusCode = HttpStatusCode.InternalServerError;
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        failure.ShouldBeAssignableTo<IOException>();
        control.ClientFaultStatusCode.ShouldBe(HttpStatusCode.BadRequest);
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A client that resets its request mid-body should report no client fault and fail the read as cancelled")]
    public async Task ClientFaultStatusCode_OnPeerResetMidBody_ShouldReportNullAndFailTheReadAsCancelled()
    {
        // Arrange — part of the body arrived and was read; the next read waits for more.
        await using Http3InMemoryPeer peer = await StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello"))));
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        await context.Request.Body.ReadExactlyAsync(new byte[5]).AsTask().WaitAsync(_timeout);
        Task<int> pending = context.Request.Body.ReadAsync(new byte[256]).AsTask();

        // Act — RESET_STREAM(H3_REQUEST_CANCELLED): the client cancels its own request (RFC 9114 §4.1.1).
        ((IMultiplexedStreamAbort)request).AbortWrite((long)Http3ErrorCode.RequestCancelled);
        Exception? failure = await CaptureFailureAsync(pending);
        bool cancelledWhenReadFailed = context.RequestCancelled.IsCancellationRequested;
        Exception? laterFailure = await TryReadToEndAsync(context.Request.Body);

        // Assert — a cancellation, as an HTTP/2 RST_STREAM is, not a client fault.
        failure.ShouldBeAssignableTo<OperationCanceledException>();
        cancelledWhenReadFailed.ShouldBeTrue();
        laterFailure.ShouldBeAssignableTo<OperationCanceledException>();
        control.ClientFaultStatusCode.ShouldBeNull();

        context.Cancel();
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A well-formed body should report no client fault")]
    public async Task ClientFaultStatusCode_OnWellFormedBody_ShouldReportNull()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await StartAsync();
        Connection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request(
                "POST", "/upload", "https", "a", headers: new Dictionary<string, string> { ["content-length"] = "5" }),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello"))));
        await request.Output.CompleteAsync();
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);

        // Act
        Exception? failure = await TryReadToEndAsync(context.Request.Body);
        await peer.ConnectionContext.SendAsync(context).AsTask().WaitAsync(_timeout);

        // Assert
        failure.ShouldBeNull();
        control.ClientFaultStatusCode.ShouldBeNull();
        (await ReadStatusAsync(request)).ShouldBe("200");
    }

    private static Task<Http3InMemoryPeer> StartAsync(Action<Http3ConnectionListenerOptions>? configure = null)
    {
        return Http3InMemoryPeer.StartAsync(configure, static options => options.Interceptors.Add(new ControlCapturingInterceptor()));
    }

    private static IHttpExchangeControl GetControl(IHttpContext context)
    {
        return context.Features.Get<CapturedControlFeature>().ShouldNotBeNull().Control;
    }

    private static async Task<string> ReadStatusAsync(Connection request)
    {
        IReadOnlyList<(long FrameType, byte[] Payload)> frames =
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request));

        return HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(frames[0].Payload)[":status"];
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

    private static async Task<Exception?> CaptureFailureAsync(Task read)
    {
        try
        {
            await read.WaitAsync(_timeout);
            return null;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
    }

    private static byte[] Combine(params byte[][] buffers)
    {
        using MemoryStream stream = new();

        foreach (byte[] buffer in buffers)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        return stream.ToArray();
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
