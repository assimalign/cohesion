using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Http;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebServerEventSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: Two composed servers report their actual bound addresses")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_TwoComposedServers_ShouldReportBoundEndPoints(bool useDefaultEndpoint)
    {
        // Arrange — initialize the TCP provider, then compare the host payload with the
        // driver's independent record of the addresses assigned by the operating system.
        await using (TcpConnectionListener warmup = TcpConnectionListener.Create(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)))
        {
            await warmup.BindAsync(CancellationToken.None);
        }
        EventSource tcpSource = EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Connections.Tcp");
        using EventSourceRecorder tcpEvents = new(tcpSource, EventLevel.Informational);
        using EventSourceRecorder serverEvents = new(WebServerEventSource.Log, EventLevel.Informational);
        WebApplicationBuilder firstBuilder = WebApplication.CreateBuilder();
        firstBuilder.Server.UseServer(options => options
            .UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            .UseHttp2(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)));
        WebApplicationBuilder secondBuilder = WebApplication.CreateBuilder();
        if (useDefaultEndpoint)
        {
            secondBuilder.Server.UseDefaultEndpoints(SeededConfiguration.Build(new Dictionary<string, string?>()),
                new IPEndPoint(IPAddress.Loopback, 0));
        }
        else
        {
            secondBuilder.Server.UseConfiguration(SeededConfiguration.Build(new Dictionary<string, string?>
            {
                ["Http:Endpoints:One:Protocol"] = "Http1",
                ["Http:Endpoints:One:Host"] = "127.0.0.1",
                ["Http:Endpoints:One:Port"] = "0",
                ["Http:Endpoints:Two:Protocol"] = "Http2",
                ["Http:Endpoints:Two:Host"] = "127.0.0.1",
                ["Http:Endpoints:Two:Port"] = "0",
            }));
        }
        await using WebApplication first = firstBuilder.Build();
        await using WebApplication second = secondBuilder.Build();
        using CancellationTokenSource timeout = new(_timeout);

        // Act
        try
        {
            await ((IWebApplication)first).StartAsync(timeout.Token);
            string[] firstAddresses = tcpEvents.Events.Where(entry => entry.EventName == "ListenerBound")
                .Select(entry => (string)entry.Payload![2]!).ToArray();
            await ((IWebApplication)second).StartAsync(timeout.Token);
            string[] secondAddresses = tcpEvents.Events.Where(entry => entry.EventName == "ListenerBound")
                .Skip(firstAddresses.Length).Select(entry => (string)entry.Payload![2]!).ToArray();

            // Assert — two listeners on the first server also exercise comma joining.
            firstAddresses.Length.ShouldBe(2);
            secondAddresses.Length.ShouldBe(useDefaultEndpoint ? 1 : 2);
            long firstId = first.Context.Servers.Single().ShouldBeOfType<WebApplicationServer>().ServerId;
            long secondId = second.Context.Servers.Single().ShouldBeOfType<WebApplicationServer>().ServerId;
            firstId.ShouldNotBe(secondId);
            foreach ((long serverId, string[] addresses) in new[] { (firstId, firstAddresses), (secondId, secondAddresses) })
            {
                foreach (string address in addresses)
                {
                    IPEndPoint.Parse(address).Port.ShouldBeGreaterThan(0);
                }
                EventWrittenEventArgs bound = serverEvents.Events.Where(entry =>
                    entry.EventId == 1 && Equals(entry.Payload![0], serverId)).ShouldHaveSingleItem();
                bound.PayloadNames!.ToArray().ShouldBe(["serverId", "protocols", "endPoints", "maxConcurrentConnections"]);
                bound.Payload![2].ShouldBe(string.Join(", ", addresses));
            }
            serverEvents.Events.ShouldNotContain(entry => entry.EventId == 0);
            tcpEvents.Events.ShouldNotContain(entry => entry.EventId == 0);
        }
        finally
        {
            await ((IWebApplication)second).StopAsync(CancellationToken.None).WaitAsync(_timeout);
            await ((IWebApplication)first).StopAsync(CancellationToken.None).WaitAsync(_timeout);
        }
    }

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
        long rejectedCredentials = source.RejectedCredentials;

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
        source.RejectedCredentials.ShouldBe(rejectedCredentials);
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
        long rejectedCredentials = source.RejectedCredentials;
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
        await recorder.WaitForCountersAsync(["connection-faults", "connection-cap-waits", "refused-response-heads", "rejected-credentials"], timeout.Token);

        // Assert
        source.ConnectionFaults.ShouldBe(faults + 1);
        source.ConnectionCapWaits.ShouldBe(capWaits + 3);
        source.RefusedResponseHeads.ShouldBe(refusedHeads + 3);
        faultCounted.ShouldBeTrue();
        peerFaultCounted.ShouldBeFalse();
        (await recorder.WaitForCounterAsync("connection-faults", 0, timeout.Token)).Value.ShouldBe((double)(faults + 1));
        (await recorder.WaitForCounterAsync("connection-cap-waits", 0, timeout.Token)).Value.ShouldBe((double)(capWaits + 3));
        (await recorder.WaitForCounterAsync("refused-response-heads", 0, timeout.Token)).Value.ShouldBe((double)(refusedHeads + 3));
        (await recorder.WaitForCounterAsync("rejected-credentials", 0, timeout.Token)).Value.ShouldBe((double)rejectedCredentials);
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
