using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[Collection(HttpTransportEventSourceTests.CollectionName)]
public class Http3TransportDiagnosticsTests
{
    private const string rejectedRequests = "cohesion.http.server.rejected_requests";
    private const string protocolErrors = "cohesion.http.server.protocol_errors";
    private const string responseRateAborts = "cohesion.http.server.response_rate_aborts";
    private const string responseAborts = "cohesion.http.server.response_aborts";
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: An oversized request head should report its 431 to both listeners")]
    public async Task ReceiveAsync_OnRejectedHead_ShouldReport431ToBothListeners()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "GET"), (":scheme", "https"), (":path", "/diagnostics-secret"),
            (":authority", "diagnostics-secret"), ("x-secret", new string('v', 300))));
        TestMultiplexedConnection transport = new(request);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(transport), static http3 => http3.QPack.MaxFieldSectionSize = 256);
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync(timeout.Token)).OpenAsync(timeout.Token);
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        long count = source.RejectedRequests;
        long measured = meter.Count(rejectedRequests, "3", statusCode: 431);

        // Act
        (await receive.MoveNextAsync()).ShouldBeFalse();

        // Assert
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(
            HttpProtocolPayloadFactory.ParseHttp3Frames(await request.ReadOutputAsync().WaitAsync(timeout.Token))[0].Payload)[":status"].ShouldBe("431");
        (source.RejectedRequests - count).ShouldBe(1);
        (meter.Count(rejectedRequests, "3", statusCode: 431) - measured).ShouldBe(1);
        EventWrittenEventArgs rejected = FindEvent(events, "RequestRejected", transport.Id);
        Payload(rejected, "streamId").ShouldBe(0L);
        Payload(rejected, "statusCode").ShouldBe(431);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: Closing a critical stream should report one connection error")]
    public async Task ReceiveAsync_OnClosedCriticalStream_ShouldReportOneConnectionErrorToBothListeners()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        TestConnection control = new(HttpProtocolPayloadFactory.CreateHttp3ControlStream((0x01, 0)), ConnectionDirection.ReadOnly);
        TestMultiplexedConnection transport = new(control);
        HttpConnectionListenerOptions options = new();
        options.UseHttp3(new TestMultiplexedConnectionListener(transport));
        await using HttpConnectionListener listener = new(options);
        Http3ConnectionContext connection = (await (await listener.AcceptOrListenAsync(timeout.Token)).OpenAsync(timeout.Token)).ShouldBeOfType<Http3ConnectionContext>();
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        long count = source.ConnectionErrors;
        long measured = meter.Count(protocolErrors, "3", errorType: "260", scope: "connection");

        // Act
        (await receive.MoveNextAsync()).ShouldBeFalse();
        await transport.ConnectionClosed.WhenCancelledAsync().WaitAsync(timeout.Token);
        connection.AbortConnection(new Http3ConnectionException(Http3ErrorCode.ClosedCriticalStream, "A repeated decision."));

        // Assert
        transport.AbortReason.ShouldBeOfType<Http3ConnectionException>().ErrorCode.ShouldBe(Http3ErrorCode.ClosedCriticalStream);
        (source.ConnectionErrors - count).ShouldBe(1);
        (meter.Count(protocolErrors, "3", errorType: "260", scope: "connection") - measured).ShouldBe(1);
        Payload(FindEvent(events, "ConnectionError", transport.Id), "errorCode").ShouldBe(260L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: A malformed body should report its first client fault and stream reset once")]
    public async Task ReadAsync_OnMalformedBody_ShouldReportOneClientFaultAndResetToBothListeners()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        await using MultiplexedStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "POST"), (":scheme", "https"), (":path", "/diagnostics-secret"),
            (":authority", "a"), ("content-length", "3")), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        long rejectedCount = source.RejectedRequests;
        long resetCount = source.StreamResets;
        long rejectedMeasured = meter.Count(rejectedRequests, "3", statusCode: 400);
        long resetMeasured = meter.Count(protocolErrors, "3", errorType: "270", scope: "stream");

        // Act — repeated reads and finalization cannot count the first fault again.
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1], timeout.Token).AsTask());
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1], timeout.Token).AsTask());
        await peer.ConnectionContext.SendAsync(context, timeout.Token);

        // Assert
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (source.RejectedRequests - rejectedCount).ShouldBe(1);
        (source.StreamResets - resetCount).ShouldBe(1);
        (meter.Count(rejectedRequests, "3", statusCode: 400) - rejectedMeasured).ShouldBe(1);
        (meter.Count(protocolErrors, "3", errorType: "270", scope: "stream") - resetMeasured).ShouldBe(1);
        Payload(FindEvent(events, "RequestRejected", peer.Server.Id), "statusCode").ShouldBe(400);
        EventWrittenEventArgs reset = FindEvent(events, "StreamReset", peer.Server.Id);
        Payload(reset, "streamId").ShouldBe(0L);
        Payload(reset, "errorCode").ShouldBe(270L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: Concurrent body failure and response timeout should claim one reset before aborting")]
    public async Task ResetRequestStream_OnConcurrentBodyFailureAndResponseTimeout_ShouldReportAndAbortOnce()
    {
        // Arrange — pause the body fault before it marks the body reset, then pause the response
        // timeout after it reports its reset but before the driver closes. Both paths saw an open
        // stream; only the first reset claim may report or choose the driver's error code.
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        await using MultiplexedStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "POST"), (":scheme", "https"), (":path", "/diagnostics-secret"),
            (":authority", "a"), ("content-length", "3")), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext exchange = await peer.NextContextAsync();
        Http3Context context = exchange.ShouldBeOfType<Http3Context>();
        Http3ConnectionContext connection = peer.ConnectionContext.ShouldBeOfType<Http3ConnectionContext>();
        using ManualResetEventSlim releaseBodyFault = new();
        using ManualResetEventSlim releaseTimeoutReset = new();
        TaskCompletionSource bodyFaultReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource timeoutResetReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int resetReports = 0;
        using MeterListener barriers = new()
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Assimalign.Cohesion.Http.Connections"
                    && instrument.Name is rejectedRequests or protocolErrors)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        barriers.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == rejectedRequests)
            {
                bodyFaultReported.TrySetResult();
                releaseBodyFault.Wait(timeout.Token);
            }
            else if (Interlocked.Increment(ref resetReports) == 1)
            {
                timeoutResetReported.TrySetResult();
                releaseTimeoutReset.Wait(timeout.Token);
            }
        });
        barriers.Start();
        long resetCount = source.StreamResets;
        long resetMeasured = meter.Count(protocolErrors, "3", scope: "stream");
        int closures = 0;
        using CancellationTokenRegistration closed = context.StreamConnection.ConnectionClosed.Register(
            () => Interlocked.Increment(ref closures));
        Task bodyRead = Task.Run(async () =>
            await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1], timeout.Token).AsTask()), timeout.Token);
        Task? slowResponse = null;

        // Act
        try
        {
            await bodyFaultReported.Task.WaitAsync(timeout.Token);
            slowResponse = Task.Run(() => connection.FailSlowResponse(context), timeout.Token);
            await timeoutResetReported.Task.WaitAsync(timeout.Token);
            releaseBodyFault.Set();
            await bodyRead.WaitAsync(timeout.Token);
        }
        finally
        {
            releaseBodyFault.Set();
            releaseTimeoutReset.Set();
            await bodyRead.WaitAsync(timeout.Token);
            if (slowResponse is not null)
            {
                await slowResponse.WaitAsync(timeout.Token);
            }
        }

        // Assert — the timeout claimed the reset first, so its code must reach the peer unchanged.
        (source.StreamResets - resetCount).ShouldBe(1);
        (meter.Count(protocolErrors, "3", scope: "stream") - resetMeasured).ShouldBe(1);
        resetReports.ShouldBe(1);
        closures.ShouldBe(1);
        ConnectionResetException reset = await Should.ThrowAsync<ConnectionResetException>(
            () => request.Input.ReadAsync(timeout.Token).AsTask());
        reset.ApplicationErrorCode.ShouldBe((long)Http3ErrorCode.RequestCancelled);
        Payload(FindEvent(events, "StreamReset", peer.Server.Id), "errorCode").ShouldBe(268L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: A malformed body read before dispatch should count only its first reset")]
    public async Task ReceiveAsync_OnMalformedBodyReadByInterceptor_ShouldReportOneResetToBothListeners()
    {
        // Arrange — the eager hook encounters a body error before an exchange exists. Its reset
        // exception then reaches the head reader, which also attempts to reset the same stream.
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        TestConnection request = new(HttpProtocolPayloadFactory.CreateHttp3RequestRaw(
            (":method", "POST"), (":scheme", "https"), (":path", "/diagnostics-secret"),
            (":authority", "a"), ("content-length", "3")));
        TestMultiplexedConnection transport = new(request);
        HttpConnectionListenerOptions options = new();
        options.Interceptors.Add(new EagerBodyReadingInterceptor());
        options.UseHttp3(new TestMultiplexedConnectionListener(transport));
        await using HttpConnectionListener listener = new(options);
        IHttpConnectionContext connection = await (await listener.AcceptOrListenAsync(timeout.Token)).OpenAsync(timeout.Token);
        await using IAsyncEnumerator<IHttpContext> receive = connection.ReceiveAsync(timeout.Token).GetAsyncEnumerator();
        long rejectedCount = source.RejectedRequests;
        long resetCount = source.StreamResets;
        long rejectedMeasured = meter.Count(rejectedRequests, "3", statusCode: 400);
        long resetMeasured = meter.Count(protocolErrors, "3", errorType: "270", scope: "stream");

        // Act
        (await receive.MoveNextAsync()).ShouldBeFalse();

        // Assert
        request.AbortReason.ShouldBeOfType<Http3StreamException>().ErrorCode.ShouldBe(Http3ErrorCode.MessageError);
        (source.RejectedRequests - rejectedCount).ShouldBe(1);
        (source.StreamResets - resetCount).ShouldBe(1);
        (meter.Count(rejectedRequests, "3", statusCode: 400) - rejectedMeasured).ShouldBe(1);
        (meter.Count(protocolErrors, "3", errorType: "270", scope: "stream") - resetMeasured).ShouldBe(1);
        Payload(FindEvent(events, "RequestRejected", transport.Id), "statusCode").ShouldBe(400);
        EventWrittenEventArgs reset = FindEvent(events, "StreamReset", transport.Id);
        Payload(reset, "streamId").ShouldBe(0L);
        Payload(reset, "errorCode").ShouldBe(270L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: A slow request body should report its rate 408 once")]
    public async Task ReadAsync_OnBodyBelowRate_ShouldReport408ToBothListeners()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            static http3 => http3.Limits.MinRequestBodyDataRate = new HttpMinDataRate(100, TimeSpan.FromMilliseconds(100)));
        await using MultiplexedStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/diagnostics-secret", "https", "a"), timeout.Token);
        await using IHttpContext context = await peer.NextContextAsync();
        long count = source.RejectedRequests;
        long measured = meter.Count(rejectedRequests, "3", statusCode: 408);

        // Act
        await Should.ThrowAsync<IOException>(() => context.Request.Body.ReadAsync(new byte[1], timeout.Token).AsTask());
        await peer.ConnectionContext.SendAsync(context, timeout.Token);

        // Assert
        HttpProtocolPayloadFactory.DecodeLiteralHttp3Headers(
            HttpProtocolPayloadFactory.ParseHttp3Frames(await Http3InMemoryPeer.ReadToEndAsync(request))[0].Payload)[":status"].ShouldBe("408");
        (source.RejectedRequests - count).ShouldBe(1);
        (meter.Count(rejectedRequests, "3", statusCode: 408) - measured).ShouldBe(1);
        EventWrittenEventArgs rejected = FindEvent(events, "RequestRejected", peer.Server.Id);
        Payload(rejected, "streamId").ShouldBe(0L);
        Payload(rejected, "statusCode").ShouldBe(408);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: A slow response reader should report the rate abort and reset")]
    public async Task WriteAsync_OnSlowResponseReader_ShouldReportAbortToBothListeners()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        StallingMultiplexedConnection? server = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            static http3 => http3.Limits.MinResponseDataRate = new HttpMinDataRate(1_000_000, TimeSpan.FromMilliseconds(100)),
            static options => options.Interceptors.Add(HttpResponseStreaming.CreateInterceptor()),
            inner => server = new StallingMultiplexedConnection(inner));
        await using MultiplexedStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/diagnostics-secret", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        await context.Response.Streaming.WriteAsync(new byte[1], timeout.Token);
        server.ShouldNotBeNull().RequestStreams[0].Writer.Stall();
        long rateCount = source.ResponseRateAborts;
        long abortCount = source.ResponseAborts;
        long rateMeasured = meter.Count(responseRateAborts, "3");
        long abortMeasured = meter.Count(responseAborts, "3");
        long resetMeasured = meter.Count(protocolErrors, "3", errorType: "268", scope: "stream");

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => context.Response.Streaming.WriteAsync(new byte[1024], timeout.Token).AsTask());
        await peer.ConnectionContext.SendAsync(context, timeout.Token);

        // Assert
        context.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        (source.ResponseRateAborts - rateCount).ShouldBe(1);
        (source.ResponseAborts - abortCount).ShouldBe(1);
        (meter.Count(responseRateAborts, "3") - rateMeasured).ShouldBe(1);
        (meter.Count(responseAborts, "3") - abortMeasured).ShouldBe(1);
        (meter.Count(protocolErrors, "3", errorType: "268", scope: "stream") - resetMeasured).ShouldBe(1);
        Payload(FindEvent(events, "ResponseRateAborted", peer.Server.Id), "streamId").ShouldBe(0L);
        Payload(FindEvent(events, "ResponseAbortedByClient", peer.Server.Id), "streamId").ShouldBe(0L);
        Payload(FindEvent(events, "StreamReset", peer.Server.Id), "errorCode").ShouldBe(268L);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/3 diagnostics: An application 400 should not count as a transport rejection")]
    public async Task SendAsync_OnApplication400_ShouldNotReportTransportRejection()
    {
        // Arrange
        using CancellationTokenSource timeout = new(_timeout);
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync();
        await using MultiplexedStream request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", "/", "https", "a"), timeout.Token);
        await request.Output.CompleteAsync();
        await using IHttpContext context = await peer.NextContextAsync();
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        long count = source.RejectedRequests;
        long measured = meter.Count(rejectedRequests, "3", statusCode: 400);

        // Act
        await peer.ConnectionContext.SendAsync(context, timeout.Token);

        // Assert
        (source.RejectedRequests - count).ShouldBe(0);
        (meter.Count(rejectedRequests, "3", statusCode: 400) - measured).ShouldBe(0);
        events.Events.ShouldNotContain(entry => entry.EventName == "RequestRejected" && Equals(Payload(entry, "connectionId"), peer.Server.Id.ToString()));
    }

    private static EventWrittenEventArgs FindEvent(EventSourceRecorder events, string name, ConnectionId connectionId)
    {
        events.Events.ShouldNotContain(static entry => entry.EventId == 0);
        EventWrittenEventArgs result = events.Events.Single(entry => entry.EventName == name && Equals(Payload(entry, "connectionId"), connectionId.ToString()));
        Payload(result, "protocol").ShouldBe("3");
        result.PayloadNames.ShouldNotBeNull().ShouldContain("suppressedCount");
        result.Payload!.OfType<string>().ShouldNotContain(static value => value.Contains("diagnostics-secret", StringComparison.Ordinal));
        return result;
    }

    private static object? Payload(EventWrittenEventArgs entry, string name)
    {
        int index = entry.PayloadNames?.IndexOf(name) ?? -1;
        return index >= 0 ? entry.Payload![index] : null;
    }

    private sealed class EagerBodyReadingInterceptor : HttpExchangeInterceptor
    {
        public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

        public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
        {
            body.CopyTo(Stream.Null);
            return body;
        }
    }
}
