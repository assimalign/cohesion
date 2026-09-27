using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.NamedPipes.Internal;

namespace Assimalign.Cohesion.Connections.NamedPipes.Tests;

[Collection(nameof(EventSourceCollection))]
public class NamedPipeConnectionEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - NamedPipeConnectionEventSource: Should be named for its assembly")]
    public void GetName_NamedPipeConnectionEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(NamedPipeConnectionEventSource));

        // Assert
        name.ShouldBe(typeof(NamedPipeConnectionEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Connections.NamedPipes");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - NamedPipeConnectionEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(NamedPipeConnectionEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Connections.NamedPipes", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - NamedPipeConnectionEventSource: Should report accepted and dialed connections once each and restore the gauge")]
    public async Task ConnectionLifecycle_ClientAndServer_ShouldReportEachTransitionOnceAndRestoreGauge()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(NamedPipeConnectionEventSource.Log, EventLevel.Verbose);
        long currentBefore = NamedPipeConnectionEventSource.Log.CurrentConnections;
        long totalBefore = NamedPipeConnectionEventSource.Log.TotalConnections;
        NamedPipeEndPoint endPoint = new(NamedPipeTestName.Create());

        NamedPipeConnectionListener listener = NamedPipeConnectionListener.Create(options => options.EndPoint = endPoint);
        await listener.BindAsync(cancellation.Token);

        ValueTask<Connection> accept = listener.AcceptAsync(cancellation.Token);
        Connection client = await new NamedPipeConnectionFactory().ConnectAsync(endPoint, cancellation.Token);
        Connection server = await accept;
        long currentWhileOpen = NamedPipeConnectionEventSource.Log.CurrentConnections;

        // Act
        client.Abort();
        await client.DisposeAsync();
        await server.DisposeAsync();
        await listener.DisposeAsync();

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 2);
        NamedPipeConnectionEventSource.Log.TotalConnections.ShouldBe(totalBefore + 2);
        NamedPipeConnectionEventSource.Log.CurrentConnections.ShouldBe(currentBefore);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        EventWrittenEventArgs bound = events.Where(e => e.EventName == "ListenerBound").ShouldHaveSingleItem();
        bound.PayloadNames.ShouldBe(["listenerId", "endPoint"]);
        bound.Payload![1].ShouldBe(endPoint.ToString());
        string listenerId = bound.Payload[0].ShouldBeOfType<string>();

        EventWrittenEventArgs serverOpened = events.Where(e => IsFor(e, "ConnectionOpened", server.Id)).ShouldHaveSingleItem();
        serverOpened.PayloadNames.ShouldBe(["connectionId", "listenerId", "localEndPoint", "remoteEndPoint"]);
        serverOpened.Payload![1].ShouldBe(listenerId);
        events.Where(e => IsFor(e, "ConnectionOpened", client.Id)).ShouldHaveSingleItem().Payload![1].ShouldBe(string.Empty);

        // The client was aborted and then disposed: the close is still reported exactly once.
        events.Where(e => IsFor(e, "ConnectionClosed", client.Id)).ShouldHaveSingleItem();
        events.Where(e => IsFor(e, "ConnectionClosed", server.Id)).ShouldHaveSingleItem();
        events.Where(e => e.EventName == "ListenerClosed").ShouldHaveSingleItem().Payload![0].ShouldBe(listenerId);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.NamedPipes] - NamedPipeConnectionEventSource: Should publish its connection counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishConnectionCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string[] counters = ["current-connections", "total-connections", "connections-per-second"];

        // Act
        using EventSourceRecorder recorder = new(NamedPipeConnectionEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, cancellation.Token);
    }

    private static bool IsFor(EventWrittenEventArgs eventData, string eventName, ConnectionId connectionId)
        => eventData.EventName == eventName && Equals(eventData.Payload![0], connectionId.ToString());
}
