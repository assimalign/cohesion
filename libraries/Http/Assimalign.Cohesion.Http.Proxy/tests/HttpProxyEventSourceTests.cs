using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http.Internal;
using Assimalign.Cohesion.Http.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Http.Tests;

[CollectionDefinition("Http proxy events", DisableParallelization = true)]
public sealed class HttpProxyEventSourceCollection { }

[Collection("Http proxy events")]
public sealed class HttpProxyEventSourceTests
{
    private static readonly Uri _destination = new("http://backend.test:8080/");

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(HttpProxyEventSource)).ShouldBe("Assimalign.Cohesion.Http.Proxy");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(HttpProxyEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: A tunnel reports opened and closed once each")]
    public async Task Lifecycle_Tunnel_ShouldReportOpenedAndClosedOnce()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        HttpProxyEventSource source = HttpProxyEventSource.Log;
        long before = source.CurrentTunnels;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        (NetworkStream destinationServer, NetworkStream destinationClient) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream destinationEnd = destinationServer;
        (NetworkStream server, NetworkStream client) = await Loopback.CreateSocketPairAsync();
        await using NetworkStream clientEnd = client;
        TestHttpContext context = new();
        context.Request.Method = HttpMethod.Connect;
        context.Features.Set(new TestProtocolUpgrade(context, HttpProtocolUpgradeKind.Connect, null, server));
        HttpTunnel tunnel = HttpTunnel.Create(new HttpTunnelOptions
        {
            ConnectCallback = (_, _) => ValueTask.FromResult<Stream>(destinationClient),
        });

        // Act
        Task<HttpProxyResult> relaying = tunnel.RelayAsync(context, new DnsEndPoint("backend.test", 443), timeout.Token);
        await clientEnd.WriteAsync(Encoding.ASCII.GetBytes("hello"), timeout.Token);
        await Loopback.ReadTextAsync(destinationEnd, 5, timeout.Token);
        long during = source.CurrentTunnels;
        destinationEnd.Socket.Shutdown(SocketShutdown.Send);
        HttpProxyResult result = await relaying;

        // Assert
        result.ShouldBe(HttpProxyResult.Completed);
        during.ShouldBe(before + 1);
        source.CurrentTunnels.ShouldBe(before);
        EventWrittenEventArgs opened = recorder.Events.Single(e => e.EventId == 5);
        opened.Level.ShouldBe(EventLevel.Verbose);
        opened.PayloadNames!.ToArray().ShouldBe(new[] { "destination" });
        opened.Payload![0].ShouldBe("backend.test:443");
        EventWrittenEventArgs closed = recorder.Events.Single(e => e.EventId == 6);
        closed.PayloadNames!.ToArray().ShouldBe(new[] { "destination", "bytesToDestination", "bytesToClient" });
        closed.Payload![0].ShouldBe("backend.test:443");
        closed.Payload[1].ShouldBe(5L);
        closed.Payload[2].ShouldBe(0L);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: A destination failure reports its type and code without its message")]
    public async Task DestinationFailed_ConnectionError_ShouldReportTypeAndCodeOncePerSecond()
    {
        // Arrange — allow the process-wide throttle from earlier tests to expire.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        HttpProxyEventSource source = HttpProxyEventSource.Log;
        long before = source.DestinationFailures;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        StubHttpMessageHandler handler = new((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "secret-message: 10.0.0.5 refused"));
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker);

        // Act
        for (int attempt = 0; attempt < 3; attempt++)
        {
            (await forwarder.ForwardAsync(new TestHttpContext(), _destination, timeout.Token)).ShouldBe(HttpProxyResult.DestinationUnreachable);
        }

        // Assert
        source.DestinationFailures.ShouldBe(before + 3);
        EventWrittenEventArgs warning = recorder.Events.Single(e => e.EventId == 1);
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.PayloadNames!.ToArray().ShouldBe(new[] { "result", "exceptionType", "errorCode", "suppressedCount" });
        warning.Payload![0].ShouldBe("DestinationUnreachable");
        warning.Payload[1].ShouldBe(typeof(HttpRequestException).FullName);
        warning.Payload[2].ShouldBe("ConnectionError");
        List<EventWrittenEventArgs> exchanges = recorder.Events.Where(e => e.EventId == 2).ToList();
        exchanges.Count.ShouldBe(3);
        exchanges[0].Level.ShouldBe(EventLevel.Verbose);
        exchanges[0].PayloadNames!.ToArray().ShouldBe(new[] { "result", "destination", "exceptionType", "errorCode" });
        exchanges[0].Payload![1].ShouldBe("backend.test:8080");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));

        // The next reported failure accounts for the two suppressed ones exactly.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await forwarder.ForwardAsync(new TestHttpContext(), _destination, timeout.Token);
        recorder.Events.Last(e => e.EventId == 1).Payload![3].ShouldBe(2);
        source.DestinationFailures.ShouldBe(before + 4);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: A buffered response over the limit reports the limit")]
    public async Task ResponseTooLarge_BufferedBody_ShouldReportLimit()
    {
        // Arrange — allow the process-wide throttle from earlier tests to expire.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        HttpProxyEventSource source = HttpProxyEventSource.Log;
        long before = source.DestinationFailures;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        StubHttpMessageHandler handler = StubHttpMessageHandler.Respond(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("hello"),
        });
        using HttpMessageInvoker invoker = new(handler);
        HttpForwarder forwarder = HttpForwarder.Create(invoker, new HttpForwarderOptions { MaxBufferedResponseBodySize = 1 });

        // Act
        HttpProxyResult first = await forwarder.ForwardAsync(new TestHttpContext(), _destination, timeout.Token);
        HttpProxyResult second = await forwarder.ForwardAsync(new TestHttpContext(), _destination, timeout.Token);

        // Assert
        first.ShouldBe(HttpProxyResult.ResponseTooLarge);
        second.ShouldBe(HttpProxyResult.ResponseTooLarge);
        source.DestinationFailures.ShouldBe(before + 2);
        EventWrittenEventArgs tooLarge = recorder.Events.Single(e => e.EventId == 4);
        tooLarge.Level.ShouldBe(EventLevel.Warning);
        tooLarge.PayloadNames!.ToArray().ShouldBe(new[] { "limit", "suppressedCount" });
        tooLarge.Payload![0].ShouldBe(1L);
        recorder.Events.Count(e => e.EventId == 2 && Equals(e.Payload![0], "ResponseTooLarge")).ShouldBe(2);
        recorder.Events.Count(e => e.EventId == 3).ShouldBeLessThanOrEqualTo(1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Proxy] - EventSource: Counters publish")]
    public async Task EnableCounters_Source_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(HttpProxyEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["current-tunnels", "destination-failures"], timeout.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }
}
