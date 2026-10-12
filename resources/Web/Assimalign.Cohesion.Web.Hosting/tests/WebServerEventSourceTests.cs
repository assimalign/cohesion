using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebServerEventSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Name equals the hosting assembly name")]
    public void GetName_ServerSource_ShouldEqualAssemblyName()
    {
        // Arrange / Act
        string name = EventSource.GetName(typeof(WebServerEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Web.Hosting");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only strict manifest validation of the statically referenced source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict manifest validation of the statically referenced source.")]
    public void GenerateManifest_ServerSource_ShouldPassStrictValidation()
    {
        // Arrange / Act
        string? manifest = EventSource.GenerateManifest(typeof(WebServerEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Two servers publish each lifecycle once with distinct identities")]
    public async Task StartAndStopAsync_TwoServers_ShouldPublishDeclaredLifecyclePayloadsOnce()
    {
        // Arrange
        WebServerEventSource source = WebServerEventSource.Log;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        WebApplicationServer first = CreateServer();
        WebApplicationServer second = CreateServer(maxConcurrentConnections: 2);
        long faults = source.ConnectionFaults;
        long capWaits = source.ConnectionCapWaits;
        long refusedHeads = source.RefusedResponseHeads;

        // Act
        try
        {
            await first.StartAsync(CancellationToken.None).WaitAsync(_timeout);
            await second.StartAsync(CancellationToken.None).WaitAsync(_timeout);
            await first.StartAsync(CancellationToken.None).WaitAsync(_timeout);
            await second.StartAsync(CancellationToken.None).WaitAsync(_timeout);
        }
        finally
        {
            await second.StopAsync(CancellationToken.None).WaitAsync(_timeout);
            await first.StopAsync(CancellationToken.None).WaitAsync(_timeout);
        }
        await second.StopAsync(CancellationToken.None).WaitAsync(_timeout);
        await first.StopAsync(CancellationToken.None).WaitAsync(_timeout);

        // Assert
        first.ServerId.ShouldNotBe(second.ServerId);
        foreach ((WebApplicationServer server, int cap) in new[] { (first, 0), (second, 2) })
        {
            EventWrittenEventArgs[] events = recorder.Events.Where(entry =>
                entry.PayloadNames is { Count: > 0 } && entry.PayloadNames[0] == "serverId" &&
                Equals(entry.Payload![0], server.ServerId)).ToArray();
            events.Select(entry => entry.EventId).ShouldBe([1, 6, 9]);
            events.ShouldAllBe(entry => entry.Level == EventLevel.Informational);
            events[0].EventName.ShouldBe("ServerBound");
            events[0].PayloadNames!.ToArray().ShouldBe(["serverId", "protocols", "endPoints", "maxConcurrentConnections"]);
            events[0].Payload!.ToArray().ShouldBe(new object?[] { server.ServerId, "HTTP/1.1", string.Empty, cap });
            events[1].EventName.ShouldBe("ServerDraining");
            events[1].PayloadNames!.ToArray().ShouldBe(["serverId", "connections", "exchanges"]);
            events[1].Payload!.ToArray().ShouldBe(new object?[] { server.ServerId, 0, 0 });
            events[2].EventName.ShouldBe("ServerStopped");
            events[2].PayloadNames!.ToArray().ShouldBe(["serverId", "elapsedMilliseconds", "aborted"]);
            events[2].Payload![1].ShouldBeOfType<double>().ShouldBeGreaterThanOrEqualTo(0);
            events[2].Payload![2].ShouldBe(false);
        }
        source.ConnectionFaults.ShouldBe(faults);
        source.ConnectionCapWaits.ShouldBe(capWaits);
        source.RefusedResponseHeads.ShouldBe(refusedHeads);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: All counters publish exact totals including work before a listener attaches")]
    public async Task OnEventCommand_LateListener_ShouldPublishAllExactCounters()
    {
        // Arrange
        WebServerEventSource source = WebServerEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long faults = source.ConnectionFaults;
        long capWaits = source.ConnectionCapWaits;
        long refusedHeads = source.RefusedResponseHeads;
        FakeHttpConnection connection = new(new FakeHttpConnectionContext());
        FakeHttpContext exchange = new();
        bool faultCounted = false;
        bool peerFaultCounted = false;
        InvalidOperationException failure = new("server operation failed");

        // Act — the typed wrappers maintain totals without reading exchange identifiers while disabled.
        source.ConnectionEnded(101, connection, null, HttpVersion.Http11, failure, false, ref faultCounted);
        source.ConnectionEnded(101, connection, null, HttpVersion.Http11, failure, false, ref faultCounted);
        source.ConnectionEnded(102, connection, null, HttpVersion.Http11, new IOException("peer closed"), false, ref peerFaultCounted);
        for (int index = 0; index < 3; index++)
        {
            source.ConnectionCapWait(101, 1);
            source.ResponseHeadRefused(101, exchange, failure);
        }
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource timeout = new(_timeout);
        await recorder.WaitForCountersAsync(["connection-faults", "connection-cap-waits", "refused-response-heads"], timeout.Token);

        // Assert
        source.ConnectionFaults.ShouldBe(faults + 1);
        source.ConnectionCapWaits.ShouldBe(capWaits + 3);
        source.RefusedResponseHeads.ShouldBe(refusedHeads + 3);
        faultCounted.ShouldBeTrue();
        peerFaultCounted.ShouldBeFalse();
        (await recorder.WaitForCounterAsync("connection-faults", 0, timeout.Token)).Value.ShouldBe((double)(faults + 1));
        (await recorder.WaitForCounterAsync("connection-cap-waits", 0, timeout.Token)).Value.ShouldBe((double)(capWaits + 3));
        (await recorder.WaitForCounterAsync("refused-response-heads", 0, timeout.Token)).Value.ShouldBe((double)(refusedHeads + 3));
        recorder.Events.ShouldBeEmpty();
    }

    private static WebApplicationServer CreateServer(int? maxConcurrentConnections = null)
        => new(new WebApplicationServerOptions
        {
            Pipeline = new FakePipeline(),
            Listener = new FakeHttpConnectionListener(),
            MaxConcurrentConnections = maxConcurrentConnections,
        });
}
