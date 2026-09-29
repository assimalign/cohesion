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

using Assimalign.Cohesion.Connections.Tcp.Internal;

namespace Assimalign.Cohesion.Connections.Tcp.Tests;

[Collection(nameof(EventSourceCollection))]
public class TcpConnectionEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should be named for its assembly")]
    public void GetName_TcpConnectionEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(TcpConnectionEventSource));

        // Assert
        name.ShouldBe(typeof(TcpConnectionEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Connections.Tcp");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(TcpConnectionEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Connections.Tcp", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should report each listener and connection transition once and restore the gauge")]
    public async Task ConnectionLifecycle_LoopbackPair_ShouldReportEachTransitionOnceAndRestoreGauge()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(TcpConnectionEventSource.Log, EventLevel.Verbose);
        long currentBefore = TcpConnectionEventSource.Log.CurrentConnections;
        long totalBefore = TcpConnectionEventSource.Log.TotalConnections;

        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await listener.BindAsync(cancellation.Token);

        ValueTask<Connection> accept = listener.AcceptAsync(cancellation.Token);
        Connection client = await new TcpConnectionFactory().ConnectAsync(listener.EndPoint, cancellation.Token);
        Connection server = await accept;
        long currentWhileOpen = TcpConnectionEventSource.Log.CurrentConnections;

        // Act
        await client.DisposeAsync();
        await server.DisposeAsync();
        await listener.DisposeAsync();

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 2);
        TcpConnectionEventSource.Log.TotalConnections.ShouldBe(totalBefore + 2);
        TcpConnectionEventSource.Log.CurrentConnections.ShouldBe(currentBefore);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        EventWrittenEventArgs bound = events.Where(e => e.EventName == "ListenerBound").ShouldHaveSingleItem();
        bound.PayloadNames.ShouldBe(["listenerId", "protocol", "endPoint"]);
        bound.Payload![1].ShouldBe("Tcp");
        bound.Payload[2].ShouldBe(listener.EndPoint.ToString());
        string listenerId = bound.Payload[0].ShouldBeOfType<string>();

        EventWrittenEventArgs serverOpened = events.Where(e => IsFor(e, "ConnectionOpened", server.Id)).ShouldHaveSingleItem();
        serverOpened.PayloadNames.ShouldBe(["connectionId", "listenerId", "protocol", "localEndPoint", "remoteEndPoint"]);
        serverOpened.Payload![1].ShouldBe(listenerId);
        serverOpened.Payload[3].ShouldBe(server.LocalEndPoint!.ToString());
        serverOpened.Payload[4].ShouldBe(server.RemoteEndPoint!.ToString());

        EventWrittenEventArgs clientOpened = events.Where(e => IsFor(e, "ConnectionOpened", client.Id)).ShouldHaveSingleItem();
        clientOpened.Payload![1].ShouldBe(string.Empty, "A dialed connection has no listener.");

        events.Where(e => IsFor(e, "ConnectionClosed", server.Id)).ShouldHaveSingleItem();
        events.Where(e => IsFor(e, "ConnectionClosed", client.Id)).ShouldHaveSingleItem();
        events.Where(e => e.EventName == "ListenerClosed").ShouldHaveSingleItem().Payload![0].ShouldBe(listenerId);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should publish its connection counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishConnectionCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string[] counters = ["current-connections", "total-connections", "connections-per-second"];

        // Act
        using EventSourceRecorder recorder = new(TcpConnectionEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, cancellation.Token);
    }

    private static bool IsFor(EventWrittenEventArgs eventData, string eventName, ConnectionId connectionId)
        => eventData.EventName == eventName && Equals(eventData.Payload![0], connectionId.ToString());
}
