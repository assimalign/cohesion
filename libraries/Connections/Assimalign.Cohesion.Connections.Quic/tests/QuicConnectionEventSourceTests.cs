using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Connections.Quic.Internal;

namespace Assimalign.Cohesion.Connections.Quic.Tests;

// The lifecycle test gates on QuicListener.IsSupported and no-ops where the platform lacks a QUIC
// implementation, like the rest of this suite; the name and manifest tests need no QUIC support.
[Collection(nameof(EventSourceCollection))]
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicConnectionEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - QuicConnectionEventSource: Should be named for its assembly")]
    public void GetName_QuicConnectionEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(QuicConnectionEventSource));

        // Assert
        name.ShouldBe(typeof(QuicConnectionEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Connections.Quic");
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - QuicConnectionEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(QuicConnectionEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Connections.Quic", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - QuicConnectionEventSource: Should report each listener, connection, and stream transition once and restore the gauges")]
    public async Task ConnectionLifecycle_LoopbackPairWithStream_ShouldReportEachTransitionOnceAndRestoreGauges()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        using X509Certificate2 certificate = QuicTestCertificate.Create();
        using EventSourceRecorder recorder = new(QuicConnectionEventSource.Log, EventLevel.Verbose);
        long connectionsBefore = QuicConnectionEventSource.Log.CurrentConnections;
        long streamsBefore = QuicConnectionEventSource.Log.CurrentStreams;
        SslApplicationProtocol applicationProtocol = new("cohesion-test");

        QuicConnectionListener listener = await QuicConnectionListener.CreateAsync(options =>
        {
            options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0);
            options.ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [applicationProtocol],
                EnabledSslProtocols = SslProtocols.Tls13
            };
        }, cancellation.Token);

        ValueTask<MultiplexedConnection> accept = listener.AcceptAsync(cancellation.Token);
        MultiplexedConnection client = await QuicConnectionFactory.Create(options =>
        {
            options.ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [applicationProtocol],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            };
        }).ConnectAsync(listener.EndPoint, cancellation.Token);
        MultiplexedConnection server = await accept;
        Connection stream = await client.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellation.Token);
        long connectionsWhileOpen = QuicConnectionEventSource.Log.CurrentConnections;
        long streamsWhileOpen = QuicConnectionEventSource.Log.CurrentStreams;

        // Act
        await stream.DisposeAsync();
        await client.DisposeAsync();
        await server.DisposeAsync();
        await listener.DisposeAsync();

        // Assert
        connectionsWhileOpen.ShouldBe(connectionsBefore + 2);
        streamsWhileOpen.ShouldBe(streamsBefore + 1);
        QuicConnectionEventSource.Log.CurrentConnections.ShouldBe(connectionsBefore);
        QuicConnectionEventSource.Log.CurrentStreams.ShouldBe(streamsBefore);

        IReadOnlyList<EventWrittenEventArgs> events = recorder.Events;
        events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        string listenerId = events.Where(e => e.EventName == "ListenerBound").ShouldHaveSingleItem().Payload![0].ShouldBeOfType<string>();
        events.Where(e => IsFor(e, "ConnectionOpened", server.Id)).ShouldHaveSingleItem().Payload![1].ShouldBe(listenerId);
        events.Where(e => IsFor(e, "ConnectionOpened", client.Id)).ShouldHaveSingleItem().Payload![1].ShouldBe(string.Empty);

        EventWrittenEventArgs streamOpened = events.Where(e => IsFor(e, "StreamOpened", stream.Id)).ShouldHaveSingleItem();
        streamOpened.PayloadNames.ShouldBe(["streamId", "connectionId", "direction"]);
        streamOpened.Payload![1].ShouldBe(client.Id.ToString());
        streamOpened.Payload[2].ShouldBe(nameof(ConnectionDirection.Bidirectional));

        events.Where(e => IsFor(e, "StreamClosed", stream.Id)).ShouldHaveSingleItem();
        events.Where(e => IsFor(e, "ConnectionClosed", server.Id)).ShouldHaveSingleItem();
        events.Where(e => IsFor(e, "ConnectionClosed", client.Id)).ShouldHaveSingleItem();
        events.Where(e => e.EventName == "ListenerClosed").ShouldHaveSingleItem().Payload![0].ShouldBe(listenerId);
    }

    [Fact(DisplayName = "Cohesion Test [Connections.Quic] - QuicConnectionEventSource: Should publish its connection and stream counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishConnectionAndStreamCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        string[] counters = ["current-connections", "total-connections", "connections-per-second", "current-streams", "streams-per-second"];

        // Act
        using EventSourceRecorder recorder = new(QuicConnectionEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, cancellation.Token);
    }

    private static bool IsFor(EventWrittenEventArgs eventData, string eventName, ConnectionId connectionId)
        => eventData.EventName == eventName && Equals(eventData.Payload![0], connectionId.ToString());
}
