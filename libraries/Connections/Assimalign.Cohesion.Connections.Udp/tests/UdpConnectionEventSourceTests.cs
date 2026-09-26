using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Udp.Internal;

namespace Assimalign.Cohesion.Connections.Udp.Tests;

[Collection(nameof(EventSourceCollection))]
public class UdpConnectionEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Udp] - UdpConnectionEventSource: Should be named for its assembly")]
    public void GetName_UdpConnectionEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(UdpConnectionEventSource));

        // Assert
        name.ShouldBe(typeof(UdpConnectionEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Connections.Udp");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Udp] - UdpConnectionEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(UdpConnectionEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Connections.Udp", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Udp] - UdpConnectionEventSource: Should report bound and connected sockets once each and restore the gauge")]
    public async Task ConnectionLifecycle_BoundAndConnectedSockets_ShouldReportEachTransitionOnceAndRestoreGauge()
    {
        // Arrange
        using EventSourceRecorder recorder = new(UdpConnectionEventSource.Log, EventLevel.Verbose);
        long currentBefore = UdpConnectionEventSource.Log.CurrentConnections;
        long totalBefore = UdpConnectionEventSource.Log.TotalConnections;
        UdpConnectionFactory factory = new();

        IDatagramConnection server = factory.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IDatagramConnection client = factory.Connect(server.LocalEndPoint);
        long currentWhileOpen = UdpConnectionEventSource.Log.CurrentConnections;

        // Act
        await client.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 2);
        UdpConnectionEventSource.Log.TotalConnections.ShouldBe(totalBefore + 2);
        UdpConnectionEventSource.Log.CurrentConnections.ShouldBe(currentBefore);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        EventWrittenEventArgs[] opened = events.Where(e => e.EventName == "ConnectionOpened").ToArray();
        opened.Length.ShouldBe(2);
        opened[0].PayloadNames.ShouldBe(["connectionId", "mode", "localEndPoint", "remoteEndPoint"]);

        EventWrittenEventArgs serverOpened = opened.Where(e => Equals(e.Payload![1], "bind")).ShouldHaveSingleItem();
        serverOpened.Payload![2].ShouldBe(server.LocalEndPoint.ToString());
        serverOpened.Payload[3].ShouldBe(string.Empty);

        EventWrittenEventArgs clientOpened = opened.Where(e => Equals(e.Payload![1], "connect")).ShouldHaveSingleItem();
        clientOpened.Payload![3].ShouldBe(server.LocalEndPoint.ToString());

        // A second DisposeAsync is a no-op: each socket's close is reported exactly once.
        string[] closedIds = events.Where(e => e.EventName == "ConnectionClosed").Select(e => (string)e.Payload![0]!).ToArray();
        closedIds.Length.ShouldBe(2);
        closedIds.ShouldBe(opened.Select(e => (string)e.Payload![0]!).ToArray(), ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Udp] - UdpConnectionEventSource: Should publish its connection counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishConnectionCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string[] counters = ["current-connections", "total-connections"];

        // Act
        using EventSourceRecorder recorder = new(UdpConnectionEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, cancellation.Token);
    }
}
