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
/// The HTTP/3 exchange control's client-fault report (#1378). A body the transport rejects while the
/// application reads it — over the size cap (<c>413</c>), below the minimum data rate (<c>408</c>), or
/// malformed: content that contradicts its Content-Length, or a malformed trailer section, which resets the
/// request stream with <c>H3_MESSAGE_ERROR</c> and is reported as <c>400</c>, or an invalid frame sequence,
/// which closes the connection and is reported as <c>400</c> too — fails the read with an
/// <see cref="IOException"/>, and <see cref="IHttpExchangeControl.ClientFaultStatusCode"/> reports the
/// status, so a host can tell the client's fault from the application's. A client that resets its own
/// request is not at fault: the read fails with the exchange's cancellation, as on HTTP/2. A connection
/// lost to an error the body had no part in also cancels the exchange and its reads (#1430).
/// </summary>
public class Http3ExchangeControlClientFaultTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    /// <summary>A connection failure raised outside the request-body reader.</summary>
    public enum ConnectionFailure
    {
        /// <summary>The peer ends its control stream.</summary>
        ControlFin,

        /// <summary>The peer resets its control stream.</summary>
        ControlReset,

        /// <summary>The peer sends DATA on its control stream.</summary>
        ForbiddenControlFrame,

        /// <summary>The connection driver reports a failure directly.</summary>
        DriverFailure
    }

    /// <summary>The application's body-read state when its connection is lost.</summary>
    public enum BodyReadState
    {
        /// <summary>No application body read has started.</summary>
        Unstarted,

        /// <summary>A read waits for the next DATA frame.</summary>
        WaitingForData,

        /// <summary>A read waits inside a partial trailing HEADERS payload.</summary>
        PartialTrailer,

        /// <summary>A complete trailer waits for a QPACK dynamic-table insertion.</summary>
        BlockedDynamicTrailer
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A body over the size cap should be reported as a 413 client fault once the read fails")]
    public async Task ClientFaultStatusCode_OnBodyOverCap_ShouldReportContentTooLarge()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await StartAsync(http3 => http3.Limits.MaxRequestBodySize = 16);
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello"))));
        IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        await context.Request.Body.ReadExactlyAsync(new byte[5]).AsTask().WaitAsync(_timeout);
        Task<int> pending = context.Request.Body.ReadAsync(new byte[256]).AsTask();

        // Act — RESET_STREAM(H3_REQUEST_CANCELLED): the client cancels its own request (RFC 9114 §4.1.1).
        request!.DirectionalAbort!.AbortWrite((long)Http3ErrorCode.RequestCancelled);
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

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A connection error outside the body should cancel the exchange and its pending and later reads")]
    [InlineData(ConnectionFailure.ControlFin, BodyReadState.Unstarted)]
    [InlineData(ConnectionFailure.ControlFin, BodyReadState.WaitingForData)]
    [InlineData(ConnectionFailure.ControlFin, BodyReadState.PartialTrailer)]
    [InlineData(ConnectionFailure.ControlFin, BodyReadState.BlockedDynamicTrailer)]
    [InlineData(ConnectionFailure.ControlReset, BodyReadState.Unstarted)]
    [InlineData(ConnectionFailure.ControlReset, BodyReadState.WaitingForData)]
    [InlineData(ConnectionFailure.ControlReset, BodyReadState.PartialTrailer)]
    [InlineData(ConnectionFailure.ControlReset, BodyReadState.BlockedDynamicTrailer)]
    [InlineData(ConnectionFailure.ForbiddenControlFrame, BodyReadState.Unstarted)]
    [InlineData(ConnectionFailure.ForbiddenControlFrame, BodyReadState.WaitingForData)]
    [InlineData(ConnectionFailure.ForbiddenControlFrame, BodyReadState.PartialTrailer)]
    [InlineData(ConnectionFailure.ForbiddenControlFrame, BodyReadState.BlockedDynamicTrailer)]
    [InlineData(ConnectionFailure.DriverFailure, BodyReadState.Unstarted)]
    [InlineData(ConnectionFailure.DriverFailure, BodyReadState.WaitingForData)]
    [InlineData(ConnectionFailure.DriverFailure, BodyReadState.PartialTrailer)]
    [InlineData(ConnectionFailure.DriverFailure, BodyReadState.BlockedDynamicTrailer)]
    public async Task ClientFaultStatusCode_OnConnectionErrorOutsideTheRead_ShouldReportNullAndCancelReads(ConnectionFailure connectionFailure, BodyReadState bodyState)
    {
        // Arrange — the priority update proves the control-stream drain is past SETTINGS before the
        // failure. An exchange exists; its body either has a read waiting or has never been read.
        using CancellationTokenSource timeout = new(_timeout);
        CodeRecordingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            configure: http3 =>
            {
                if (bodyState == BodyReadState.BlockedDynamicTrailer)
                {
                    http3.QPack.MaxTableCapacity = 4096;
                    http3.QPack.MaxBlockedStreams = 16;
                }
            },
            configureListener: static options => options.Interceptors.Add(new ControlCapturingInterceptor()),
            decorate: inner => server = new CodeRecordingMultiplexedConnection(inner));
        await using MultiplexedConnectionStream controlStream = await peer.OpenUnidirectionalStreamAsync();
        await controlStream.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3ControlStreamWithControlFrames(
            new (long, long)[] { (0x01, 0) },
            ((long)Http3FrameType.PriorityUpdateRequest, HttpProtocolPayloadFactory.CreateHttp3PriorityUpdatePayload(0, "u=1"))), timeout.Token);
        await using MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(Combine(
            HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/upload", "https", "a"),
            HttpProtocolPayloadFactory.CreateHttp3Frame(0x0, Encoding.ASCII.GetBytes("hello"))), timeout.Token);
        await using IHttpContext context = await peer.NextContextAsync();
        IHttpExchangeControl control = GetControl(context);
        Http3ConnectionContext connectionContext = peer.ConnectionContext.ShouldBeOfType<Http3ConnectionContext>();
        while (!connectionContext.TryGetRequestStreamPriority(0, out _))
        {
            await Task.Delay(10, timeout.Token);
        }

        Task<int>? pending = null;
        if (bodyState != BodyReadState.Unstarted)
        {
            await context.Request.Body.ReadExactlyAsync(new byte[5], timeout.Token);
            if (bodyState == BodyReadState.PartialTrailer)
            {
                // A two-octet QPACK field-section prefix cut off after its first octet.
                await request.Output.WriteAsync(Combine(
                    HttpProtocolPayloadFactory.CreateHttp3FrameHeader((long)Http3FrameType.Headers, 2),
                    [0x00]), timeout.Token);
            }
            else if (bodyState == BodyReadState.BlockedDynamicTrailer)
            {
                // Entry 0 has not been inserted: encoded Required Insert Count 2 means one insert
                // is required. The frame is complete, so only the dynamic-table decoder can wait.
                await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3DynamicRequest(
                    encodedRequiredInsertCount: 2,
                    deltaBaseByte: 0x00,
                    literalFields: [],
                    0), timeout.Token);
            }

            pending = context.Request.Body.ReadAsync(new byte[256]).AsTask();
            pending.IsCompleted.ShouldBeFalse();
        }

        CancellationToken requestCancelled = context.RequestCancelled;
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = requestCancelled.Register(() => cancellationObserved.TrySetResult());
        requestCancelled.IsCancellationRequested.ShouldBeFalse();

        // Act — none of these failures comes from reading this request's body.
        server.ShouldNotBeNull();
        Http3ErrorCode expectedCode = Http3ErrorCode.ClosedCriticalStream;
        switch (connectionFailure)
        {
            case ConnectionFailure.ControlFin:
                await controlStream.Output.CompleteAsync();
                break;
            case ConnectionFailure.ControlReset:
                controlStream!.DirectionalAbort!.AbortWrite((long)Http3ErrorCode.RequestCancelled);
                break;
            case ConnectionFailure.ForbiddenControlFrame:
                expectedCode = Http3ErrorCode.UnexpectedFrame;
                try
                {
                    await controlStream.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Frame(0x00, []), timeout.Token);
                }
                catch (ConnectionResetException exception) when (exception.ApplicationErrorCode == (long)expectedCode)
                {
                    // The forbidden frame can close the connection before its flush completes.
                }
                break;
            case ConnectionFailure.DriverFailure:
                expectedCode = Http3ErrorCode.ProtocolError;
                server.Abort((long)expectedCode, new Http3ConnectionException(expectedCode, "The connection failed outside the request body."));
                break;
        }

        await server.ConnectionClosed.WhenCancelledAsync().WaitAsync(timeout.Token);
        await Task.WhenAny(cancellationObserved.Task, Task.Delay(TimeSpan.FromSeconds(1), timeout.Token));

        // Assert — cancellation is observable even when the application never touched the body. A
        // pending read and subsequent reads fail with that exact exchange token, without a body fault.
        server.AbortErrorCode.ShouldBe((long)expectedCode);
        cancellationObserved.Task.IsCompleted.ShouldBeTrue();
        requestCancelled.IsCancellationRequested.ShouldBeTrue();
        if (pending is not null)
        {
            Exception? failure = await CaptureFailureAsync(pending);
            failure.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(requestCancelled);
        }

        Exception? laterFailure = await CaptureFailureAsync(context.Request.Body.ReadAsync(new byte[256]).AsTask());
        laterFailure.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(requestCancelled);
        control.ClientFaultStatusCode.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3 Client Fault: A well-formed body should report no client fault")]
    public async Task ClientFaultStatusCode_OnWellFormedBody_ShouldReportNull()
    {
        // Arrange
        await using Http3InMemoryPeer peer = await StartAsync();
        MultiplexedConnectionStream request = await peer.OpenRequestStreamAsync();
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

    private static async Task<string> ReadStatusAsync(MultiplexedConnectionStream request)
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
