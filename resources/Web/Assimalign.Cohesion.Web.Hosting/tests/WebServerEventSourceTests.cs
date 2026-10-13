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
using Assimalign.Cohesion.Http.Connections;
using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Hosting.Internal;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

[Collection(nameof(TelemetryCollection))]
public sealed class WebServerEventSourceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - EventSource: A raw HTTP listener registration reports its bound endpoint")]
    public async Task StartAsync_WithRawHttpRegistration_ShouldReportBoundEndPoint()
    {
        // Arrange
        TcpConnectionListener transport = TcpConnectionListener.Create(
            options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        using EventSourceRecorder recorded = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Informational);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer((HttpConnectionListenerOptions options) => options.UseHttp1(() => transport));
        await using WebApplication app = builder.Build();
        using CancellationTokenSource timeout = new(_timeout);

        // Act
        try
        {
            await ((IWebApplication)app).StartAsync(timeout.Token);

            // Assert
            ((IPEndPoint)transport.EndPoint).Port.ShouldBeGreaterThan(0);
            EventWrittenEventArgs bound = recorded.Events.Where(entry => entry.EventId == 1).ShouldHaveSingleItem();
            bound.Payload![2].ShouldBe(transport.EndPoint.ToString());
        }
        finally
        {
            await ((IWebApplication)app).StopAsync(CancellationToken.None).WaitAsync(_timeout);
        }
    }

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
        using EventSourceRecorder serverEvents = new(await ServerDiagnosticsSource.GetAsync(), EventLevel.Informational);
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
            long firstId = (long)serverEvents.Events.Single(entry => entry.EventId == 1 &&
                Equals(entry.Payload![2], string.Join(", ", firstAddresses))).Payload![0]!;
            long secondId = (long)serverEvents.Events.Single(entry => entry.EventId == 1 &&
                Equals(entry.Payload![2], string.Join(", ", secondAddresses))).Payload![0]!;
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


}
