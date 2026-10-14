using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Connections.Tests;

[Collection(HttpTransportEventSourceTests.CollectionName)]
public class HttpTransportEventSourceTests
{
    public const string CollectionName = "HTTP transport diagnostics";

    private const string rejectedRequests = "cohesion.http.server.rejected_requests";
    private const string protocolErrors = "cohesion.http.server.protocol_errors";
    private const string responseRateAborts = "cohesion.http.server.response_rate_aborts";
    private const string responseAborts = "cohesion.http.server.response_aborts";

    internal static Task WaitForThrottleAsync() => Task.Delay(TimeSpan.FromMilliseconds(1100));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Should use the final assembly name")]
    public void GetName_TransportSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(HttpTransportEventSource));

        // Assert
        name.ShouldBe(typeof(HttpTransportEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Http.Connections");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Should generate a strict manifest")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(HttpTransportEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Http.Connections", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Should deliver every counter after a listener attaches")]
    public async Task OnEventCommand_CountersEnabled_ShouldPublishAllCounters()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder events = new(HttpTransportEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act
        await events.WaitForCountersAsync(
            ["rejected-requests", "connection-errors", "stream-resets", "response-rate-aborts", "response-aborts"], timeout.Token);

        // Assert
        events.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Should publish exact bounded events and Meter measurements")]
    public async Task Report_EveryOutcome_ShouldPublishBoundedPayloadsAndExactDeltas()
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        ConnectionId connectionId = ConnectionId.New();
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        long rejectedBefore = source.RejectedRequests;
        long connectionsBefore = source.ConnectionErrors;
        long streamsBefore = source.StreamResets;
        long ratesBefore = source.ResponseRateAborts;
        long responsesBefore = source.ResponseAborts;
        long rejectedMeterBefore = meter.Count(rejectedRequests, "1.1", statusCode: 431);
        long connectionsMeterBefore = meter.Count(protocolErrors, "2", errorType: "11", scope: "connection");
        long streamsMeterBefore = meter.Count(protocolErrors, "3", errorType: "269", scope: "stream");
        long ratesMeterBefore = meter.Count(responseRateAborts, "2");
        long responsesMeterBefore = meter.Count(responseAborts, "3");

        // Act
        source.RequestRejected(connectionId, "1.1", 0, 431);
        source.ConnectionError(connectionId, "2", 11);
        source.StreamReset(connectionId, "3", 4, 269);
        source.ResponseRateAborted(connectionId, "2", 7);
        source.ResponseAbortedByClient(connectionId, "3", 8);

        // Assert
        source.RejectedRequests.ShouldBe(rejectedBefore + 1);
        source.ConnectionErrors.ShouldBe(connectionsBefore + 1);
        source.StreamResets.ShouldBe(streamsBefore + 1);
        source.ResponseRateAborts.ShouldBe(ratesBefore + 1);
        source.ResponseAborts.ShouldBe(responsesBefore + 1);
        meter.Count(rejectedRequests, "1.1", statusCode: 431).ShouldBe(rejectedMeterBefore + 1);
        meter.Count(protocolErrors, "2", errorType: "11", scope: "connection").ShouldBe(connectionsMeterBefore + 1);
        meter.Count(protocolErrors, "3", errorType: "269", scope: "stream").ShouldBe(streamsMeterBefore + 1);
        meter.Count(responseRateAborts, "2").ShouldBe(ratesMeterBefore + 1);
        meter.Count(responseAborts, "3").ShouldBe(responsesMeterBefore + 1);

        EventWrittenEventArgs[] outcomes = events.Events.Where(item => item.Payload is { Count: > 0 } && Equals(item.Payload[0], connectionId.ToString())).ToArray();
        outcomes.Length.ShouldBe(5);
        AssertEvent(outcomes, 1, "RequestRejected", ["connectionId", "protocol", "streamId", "statusCode", "suppressedCount"], [connectionId.ToString(), "1.1", 0L, 431]);
        AssertEvent(outcomes, 2, "ConnectionError", ["connectionId", "protocol", "errorCode", "suppressedCount"], [connectionId.ToString(), "2", 11L]);
        AssertEvent(outcomes, 3, "StreamReset", ["connectionId", "protocol", "streamId", "errorCode", "suppressedCount"], [connectionId.ToString(), "3", 4L, 269L]);
        AssertEvent(outcomes, 4, "ResponseRateAborted", ["connectionId", "protocol", "streamId", "suppressedCount"], [connectionId.ToString(), "2", 7L]);
        AssertEvent(outcomes, 5, "ResponseAbortedByClient", ["connectionId", "protocol", "streamId", "suppressedCount"], [connectionId.ToString(), "3", 8L]);
        events.Events.ShouldNotContain(item => item.EventId == 0);

        // Exact schemas exclude peer addresses, request targets, headers, bodies and exception text.
        TransportMeterRecorder.Measurement rejected = meter.Measurements.Where(item => item.Name == rejectedRequests).ShouldHaveSingleItem();
        rejected.Unit.ShouldBe("{request}");
        rejected.Tags.Keys.Order().ShouldBe(["cohesion.http.rejection.status_code", "network.protocol.version"]);
        foreach (TransportMeterRecorder.Measurement error in meter.Measurements.Where(item => item.Name == protocolErrors))
        {
            error.Unit.ShouldBe("{error}");
            error.Tags.Keys.Order().ShouldBe(["cohesion.http.error.scope", "error.type", "network.protocol.version"]);
        }
        foreach (TransportMeterRecorder.Measurement response in meter.Measurements.Where(item => item.Name is responseRateAborts or responseAborts))
        {
            response.Unit.ShouldBe(response.Name == responseRateAborts ? "{abort}" : "{response}");
            response.Tags.Keys.ShouldBe(["network.protocol.version"]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Repeatable outcomes should throttle events and retain exact counters")]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Report_RepeatedOutcome_ShouldReportSuppressionAndCountEveryOccurrence(int eventId)
    {
        // Arrange
        await WaitForThrottleAsync();
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        ConnectionId connectionId = ConnectionId.New();
        using EventSourceRecorder events = new(source, EventLevel.Verbose);
        using TransportMeterRecorder meter = new();
        long sourceBefore = ReadCounter(source, eventId);
        string instrumentName = eventId switch
        {
            1 => rejectedRequests,
            2 or 3 => protocolErrors,
            4 => responseRateAborts,
            _ => responseAborts
        };
        long meterBefore = meter.Count(instrumentName, "2");

        // Act
        Report(source, connectionId, eventId);
        Report(source, connectionId, eventId);
        Report(source, connectionId, eventId);
        EventWrittenEventArgs first = events.Events.Where(item => IsFor(item, eventId, connectionId)).ShouldHaveSingleItem();
        await WaitForThrottleAsync();
        Report(source, connectionId, eventId);

        // Assert
        EventWrittenEventArgs[] outcomes = events.Events.Where(item => IsFor(item, eventId, connectionId)).ToArray();
        outcomes.Length.ShouldBe(2);
        outcomes[0].ShouldBeSameAs(first);
        outcomes[1].Payload![^1].ShouldBe(2);
        ReadCounter(source, eventId).ShouldBe(sourceBefore + 4);
        meter.Count(instrumentName, "2").ShouldBe(meterBefore + 4);
        events.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Transport EventSource: Disabled EventSource and Meter should allocate no memory")]
    public void Report_NoListeners_ShouldAllocateNothingAndKeepCountersExact()
    {
        // Arrange
        HttpTransportEventSource source = HttpTransportEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        ConnectionId connectionId = ConnectionId.New();
        for (int i = 0; i < 1000; i++)
        {
            ReportAll(source, connectionId);
        }

        long rejectedBefore = source.RejectedRequests;
        long connectionsBefore = source.ConnectionErrors;
        long streamsBefore = source.StreamResets;
        long ratesBefore = source.ResponseRateAborts;
        long responsesBefore = source.ResponseAborts;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (int i = 0; i < 10_000; i++)
        {
            ReportAll(source, connectionId);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        allocated.ShouldBe(0);
        source.RejectedRequests.ShouldBe(rejectedBefore + 10_000);
        source.ConnectionErrors.ShouldBe(connectionsBefore + 10_000);
        source.StreamResets.ShouldBe(streamsBefore + 10_000);
        source.ResponseRateAborts.ShouldBe(ratesBefore + 10_000);
        source.ResponseAborts.ShouldBe(responsesBefore + 10_000);
    }

    private static void AssertEvent(EventWrittenEventArgs[] outcomes, int eventId, string name, string[] payloadNames, object[] values)
    {
        EventWrittenEventArgs outcome = outcomes.Where(item => item.EventId == eventId).ShouldHaveSingleItem();
        outcome.EventName.ShouldBe(name);
        outcome.Level.ShouldBe(EventLevel.Warning);
        outcome.PayloadNames.ShouldBe(payloadNames);
        outcome.Payload!.Take(values.Length).ShouldBe(values);
        outcome.Payload![^1].ShouldBeOfType<int>();
    }

    private static bool IsFor(EventWrittenEventArgs item, int eventId, ConnectionId connectionId)
        => item.EventId == eventId && Equals(item.Payload![0], connectionId.ToString());

    private static long ReadCounter(HttpTransportEventSource source, int eventId)
        => eventId switch
        {
            1 => source.RejectedRequests,
            2 => source.ConnectionErrors,
            3 => source.StreamResets,
            4 => source.ResponseRateAborts,
            _ => source.ResponseAborts
        };

    private static void Report(HttpTransportEventSource source, ConnectionId connectionId, int eventId)
    {
        switch (eventId)
        {
            case 1: source.RequestRejected(connectionId, "2", 1, 400); break;
            case 2: source.ConnectionError(connectionId, "2", 1); break;
            case 3: source.StreamReset(connectionId, "2", 1, 1); break;
            case 4: source.ResponseRateAborted(connectionId, "2", 1); break;
            case 5: source.ResponseAbortedByClient(connectionId, "2", 1); break;
        }
    }

    private static void ReportAll(HttpTransportEventSource source, ConnectionId connectionId)
    {
        source.RequestRejected(connectionId, "1.1", 0, 400);
        source.ConnectionError(connectionId, "2", 11);
        source.StreamReset(connectionId, "3", 4, 269);
        source.ResponseRateAborted(connectionId, "2", 7);
        source.ResponseAbortedByClient(connectionId, "3", 8);
    }
}
