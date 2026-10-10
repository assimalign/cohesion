using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Tcp.Internal;
using Assimalign.Cohesion.Connections.Tcp.Tests.TestObjects;

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

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should report each queued connection the listener skipped")]
    public async Task AcceptSkipped_ClientsResetWhileQueued_ShouldReportEachSkippedConnection()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(TcpConnectionEventSource.Log, EventLevel.Verbose);

        await using TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await listener.BindAsync(cancellation.Token);

        const int resetClients = 4;

        for (int i = 0; i < resetClients; i++)
        {
            using Socket reset = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await reset.ConnectAsync(listener.EndPoint, cancellation.Token);
            reset.LingerState = new LingerOption(true, 0);
        }

        using Socket healthy = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await healthy.ConnectAsync(listener.EndPoint, cancellation.Token);
        int healthyPort = ((IPEndPoint)healthy.LocalEndPoint!).Port;

        // Act
        int resetConnectionsReturned = 0;
        Connection? accepted = null;

        for (int i = 0; i <= resetClients && accepted is null; i++)
        {
            Connection connection = await listener.AcceptAsync(cancellation.Token);

            if (connection.RemoteEndPoint is IPEndPoint remote && remote.Port == healthyPort)
            {
                accepted = connection;
            }
            else
            {
                resetConnectionsReturned++;
                await connection.DisposeAsync();
            }
        }

        // Assert
        accepted.ShouldNotBeNull();
        await accepted.DisposeAsync();

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        string listenerId = events
            .Where(e => e.EventName == "ListenerBound" && Equals(e.Payload![2], listener.EndPoint.ToString()))
            .ShouldHaveSingleItem()
            .Payload![0]
            .ShouldBeOfType<string>();

        EventWrittenEventArgs[] skipped = events
            .Where(e => e.EventName == "AcceptSkipped" && Equals(e.Payload![0], listenerId))
            .ToArray();

        foreach (EventWrittenEventArgs skip in skipped)
        {
            skip.PayloadNames.ShouldBe(["listenerId", "socketError"]);
            skip.Payload![1].ShouldBeOneOf(nameof(SocketError.ConnectionReset), nameof(SocketError.ConnectionAborted));
        }

        (skipped.Length + resetConnectionsReturned).ShouldBeLessThanOrEqualTo(resetClients);

        if (OperatingSystem.IsWindows())
        {
            // Windows fails the accept of every connection reset while it was queued.
            skipped.Length.ShouldBe(resetClients);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should report the first of a burst of accept back-offs and hold back the rest")]
    public async Task AcceptBackoff_BurstOfResourceFailures_ShouldReportFirstBackoffOnly()
    {
        // Arrange — three back-offs (5, 10 and 20 ms) fall inside one report interval.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(TcpConnectionEventSource.Log, EventLevel.Verbose);
        ScriptedAccept accept = new(SocketError.TooManyOpenSockets, SocketError.NoBufferSpaceAvailable, SocketError.TooManyOpenSockets);

        await using TcpConnectionListener listener = new(
            new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            accept.AcceptAsync);
        await listener.BindAsync(cancellation.Token);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint, cancellation.Token);

        // Act
        await using Connection server = await listener.AcceptAsync(cancellation.Token);

        // Assert
        accept.Attempts.ShouldBe(4);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        string listenerId = events
            .Where(e => e.EventName == "ListenerBound" && Equals(e.Payload![2], listener.EndPoint.ToString()))
            .ShouldHaveSingleItem()
            .Payload![0]
            .ShouldBeOfType<string>();

        EventWrittenEventArgs backoff = events
            .Where(e => e.EventName == "AcceptBackoff" && Equals(e.Payload![0], listenerId))
            .ShouldHaveSingleItem();

        backoff.EventId.ShouldBe(11);
        backoff.Level.ShouldBe(EventLevel.Warning);
        backoff.PayloadNames.ShouldBe(["listenerId", "socketError", "delayMilliseconds", "unreportedBackoffs"]);
        backoff.Payload![1].ShouldBe(nameof(SocketError.TooManyOpenSockets));
        backoff.Payload[2].ShouldBe(5);
        backoff.Payload[3].ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Tcp] - TcpConnectionEventSource: Should report an accepted connection the listener could not set up")]
    public async Task AcceptedConnectionDropped_SetupFails_ShouldReportDroppedConnection()
    {
        // Arrange — setting TCP_NODELAY on a bound UDP socket fails on every platform, which makes the set-up
        // of an "accepted" socket fail the way a client reset can on macOS.
        using CancellationTokenSource cancellation = new(_testTimeout);
        using EventSourceRecorder recorder = new(TcpConnectionEventSource.Log, EventLevel.Verbose);

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        SocketError setupError = Should.Throw<SocketException>(() => probe.NoDelay = true).SocketErrorCode;

        using Socket unusable = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        unusable.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        ScriptedAccept accept = new();
        accept.Return(unusable);

        await using TcpConnectionListener listener = new(
            new TcpConnectionListenerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) },
            accept.AcceptAsync);
        await listener.BindAsync(cancellation.Token);

        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.EndPoint, cancellation.Token);

        // Act
        await using Connection server = await listener.AcceptAsync(cancellation.Token);

        // Assert
        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        string listenerId = events
            .Where(e => e.EventName == "ListenerBound" && Equals(e.Payload![2], listener.EndPoint.ToString()))
            .ShouldHaveSingleItem()
            .Payload![0]
            .ShouldBeOfType<string>();

        EventWrittenEventArgs dropped = events
            .Where(e => e.EventName == "AcceptedConnectionDropped" && Equals(e.Payload![0], listenerId))
            .ShouldHaveSingleItem();

        dropped.EventId.ShouldBe(12);
        dropped.Level.ShouldBe(EventLevel.Verbose);
        dropped.PayloadNames.ShouldBe(["listenerId", "socketError"]);
        dropped.Payload![1].ShouldBe(setupError.ToString());
        events.ShouldNotContain(e => e.EventName == "AcceptSkipped" && Equals(e.Payload![0], listenerId));
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
