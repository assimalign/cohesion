using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[Collection(HttpTransportEventSourceTests.CollectionName)]
public class Http2TransportDiagnosticsTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(1_000_000, TimeSpan.FromMilliseconds(300));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/2 transport diagnostics: A cleartext HTTPS rejection publishes both signals once")]
    public async Task ReceiveAsync_OnMisdirectedRequest_ShouldReportRejection()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        long before = meter.Count("cohesion.http.server.rejected_requests", "2", 421);

        // Act — the follow-up request proves the rejected stream never reached dispatch.
        await peer.SendHeadersAsync(1, true, (":method", "GET"), (":scheme", "https"),
            (":path", "/private-target"), (":authority", "secret.invalid"));
        await peer.SendHeadersAsync(3, true, Http2TestPeer.Get("/accepted"));
        IHttpContext accepted = await peer.ReceiveContextAsync();
        await peer.SyncAsync();

        // Assert
        accepted.Request.Path.Value.ShouldBe("/accepted");
        meter.Count("cohesion.http.server.rejected_requests", "2", 421).ShouldBe(before + 1);
        EventWrittenEventArgs rejected = SingleEvent(events, 1, peer, 1);
        Payload(rejected, "statusCode").ShouldBe(421);
        rejected.Payload!.OfType<string>().ShouldNotContain(value => value.Contains("private-target", StringComparison.Ordinal)
            || value.Contains("secret.invalid", StringComparison.Ordinal));
        await peer.ConnectionContext.SendAsync(accepted, CancellationToken.None).AsTask().WaitAsync(_timeout);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/2 transport diagnostics: A connection protocol error publishes its code")]
    public async Task ReceiveAsync_OnConnectionProtocolError_ShouldReportCode()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync();
        long before = meter.Count("cohesion.http.server.protocol_errors", "2", errorType: "1", scope: "connection");

        // Act — PING must use stream zero.
        await peer.SendAsync(Http2TestSettings.RawFrame(0x6, 0, 1, new byte[8]));
        await peer.WaitForReceiveEndAsync();

        // Assert
        meter.Count("cohesion.http.server.protocol_errors", "2", errorType: "1", scope: "connection").ShouldBe(before + 1);
        EventWrittenEventArgs error = SingleEvent(events, 2, peer);
        Payload(error, "errorCode").ShouldBe((long)Http2ErrorCode.ProtocolError);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/2 transport diagnostics: A rate 408 is counted once across its fault latch and response")]
    public async Task ReadAsync_OnRequestBelowRate_ShouldReportOneRejection()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(configure: options => options.Limits.MinRequestBodyDataRate = _rate);
        await peer.SendHeadersAsync(1, false, Http2TestPeer.Request("POST", "/upload"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        long before = meter.Count("cohesion.http.server.rejected_requests", "2", 408);

        // Act — the client never supplies its body.
        await Should.ThrowAsync<IOException>(() => exchange.Request.Body.ReadAsync(new byte[1], CancellationToken.None).AsTask().WaitAsync(_timeout));
        await peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        meter.Count("cohesion.http.server.rejected_requests", "2", 408).ShouldBe(before + 1);
        Payload(SingleEvent(events, 1, peer, 1), "statusCode").ShouldBe(408);
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.RequestTimeout);
        events.Events.ShouldNotContain(item => item.EventId == 3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/2 transport diagnostics: A slow response records its rate abort and reset once")]
    public async Task SendAsync_OnResponseBelowRate_ShouldReportRateAbortAndReset()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(
            configure: options => options.Limits.MinResponseDataRate = _rate, initialWindowSize: 0);
        await peer.SendHeadersAsync(1, true, Http2TestPeer.Get("/response"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(new byte[1024]);
        long rateBefore = meter.Count("cohesion.http.server.response_rate_aborts", "2");
        long resetBefore = meter.Count("cohesion.http.server.protocol_errors", "2", errorType: "8", scope: "stream");
        long abortBefore = meter.Count("cohesion.http.server.response_aborts", "2");

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask().WaitAsync(_timeout));
        await peer.SyncAsync();

        // Assert
        meter.Count("cohesion.http.server.response_rate_aborts", "2").ShouldBe(rateBefore + 1);
        meter.Count("cohesion.http.server.protocol_errors", "2", errorType: "8", scope: "stream").ShouldBe(resetBefore + 1);
        meter.Count("cohesion.http.server.response_aborts", "2").ShouldBe(abortBefore + 1);
        SingleEvent(events, 4, peer, 1);
        SingleEvent(events, 5, peer, 1);
        Payload(SingleEvent(events, 3, peer, 1), "errorCode").ShouldBe((long)Http2ErrorCode.Cancel);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - HTTP/2 transport diagnostics: A peer reset reports the response fault without a server protocol error")]
    public async Task SendAsync_OnPeerReset_ShouldReportResponseAbort()
    {
        // Arrange
        await HttpTransportEventSourceTests.WaitForThrottleAsync();
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        await using Http2TestPeer peer = await Http2TestPeer.ConnectAsync(initialWindowSize: 0);
        await peer.SendHeadersAsync(1, true, Http2TestPeer.Get("/response"));
        IHttpContext exchange = await peer.ReceiveContextAsync();
        exchange.Response.Body = new MemoryStream(new byte[1024]);
        long before = meter.Count("cohesion.http.server.response_aborts", "2");
        Task send = peer.ConnectionContext.SendAsync(exchange, CancellationToken.None).AsTask();
        await peer.Output.ReadUntilAsync(frames => frames.Any(frame => frame.IsHeaders && frame.StreamId == 1), "the response head");

        // Act
        await peer.SendRstStreamAsync(1, Http2ErrorCode.Cancel);
        await send.WaitAsync(_timeout);
        await peer.SyncAsync();

        // Assert
        meter.Count("cohesion.http.server.response_aborts", "2").ShouldBe(before + 1);
        SingleEvent(events, 5, peer, 1);
        events.Events.ShouldNotContain(item => item.EventId == 2 || item.EventId == 3 || item.EventId == 4);
    }

    private static EventWrittenEventArgs SingleEvent(EventSourceRecorder recorder, int eventId, Http2TestPeer peer, long? streamId = null)
    {
        EventWrittenEventArgs[] matches = recorder.Events.Where(item => item.EventId == eventId
            && Equals(Payload(item, "connectionId"), peer.Transport.Id.ToString())
            && (streamId is null || Equals(Payload(item, "streamId"), streamId.Value))).ToArray();
        matches.Length.ShouldBe(1);
        Payload(matches[0], "protocol").ShouldBe("2");
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
        return matches[0];
    }

    private static object? Payload(EventWrittenEventArgs item, string name)
        => item.Payload![item.PayloadNames!.IndexOf(name)];
}
