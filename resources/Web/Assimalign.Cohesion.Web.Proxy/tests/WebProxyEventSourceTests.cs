using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Proxy.Internal;
using Assimalign.Cohesion.Web.Proxy.Tests.TestObjects;

using Shouldly;

using Xunit;

using NetHttpStatusCode = System.Net.HttpStatusCode;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// The forward proxy's event source: its name and manifest, the refusals it reports with their
/// payloads, the counters it keeps whether or not anyone listens, and the counters it publishes.
/// </summary>
[Collection("Web.Proxy diagnostics")]
public sealed class WebProxyEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(WebProxyEventSource)).ShouldBe("Assimalign.Cohesion.Web.Proxy");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - EventSource: Strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest validation of the statically named event source.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest validation of the statically named event source.")]
    public void GenerateManifest_Source_ShouldBeValid()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(WebProxyEventSource), null, EventManifestOptions.Strict).ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Refusal: A target no rule allows is reported once with its host, port and request")]
    public async Task UseForwardProxy_TargetNotAllowed_ShouldWriteCorrelatedRefusal()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => Task.CompletedTask, cancellation.Token);
        string requestId = string.Empty;
        await using ProxyTestServer proxy = await StartProxyAsync(
            new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] },
            id => requestId = id,
            cancellation.Token);
        using HttpClient client = CreateClient(proxy);
        long before = WebProxyEventSource.Log.RefusedRequests;

        // Act
        using HttpResponseMessage response = await client.GetAsync($"http://localhost:{backend.Port}/path?query=secret", cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.Forbidden);
        WebProxyEventSource.Log.RefusedRequests.ShouldBe(before + 1);
        requestId.Length.ShouldBe(32);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 1 && (string)e.Payload![3]! == requestId);
        entry.EventName.ShouldBe("RequestRefused");
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "host", "port", "requestId" });
        entry.Payload![0].ShouldBe("NotAllowed");
        entry.Payload[1].ShouldBe("localhost");
        entry.Payload[2].ShouldBe(backend.Port);
        recorder.Events.ShouldNotContain(e => e.Payload != null && e.Payload.OfType<string>().Any(value => value.Contains("secret", StringComparison.Ordinal)));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Refusal: A loopback literal allowed only by a wildcard is reported as a refused address")]
    public async Task UseForwardProxy_WildcardToLoopbackLiteral_ShouldWriteAddressRefusal()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer echo = TcpEchoServer.Start();
        string requestId = string.Empty;
        await using ProxyTestServer proxy = await StartProxyAsync(
            new ForwardProxyRule { Hosts = ["*"], Ports = [echo.Port] },
            id => requestId = id,
            cancellation.Token);
        await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);
        long before = WebProxyEventSource.Log.RefusedRequests;

        // Act
        (int status, _) = await client.SendAsync($"CONNECT 127.0.0.1:{echo.Port} HTTP/1.1\r\nHost: 127.0.0.1:{echo.Port}\r\n\r\n", cancellation.Token);

        // Assert
        status.ShouldBe(403);
        echo.Accepted.ShouldBe(0);
        WebProxyEventSource.Log.RefusedRequests.ShouldBe(before + 1);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 1 && (string)e.Payload![3]! == requestId);
        entry.Payload![0].ShouldBe("AddressRefused");
        entry.Payload[1].ShouldBe("127.0.0.1");
        entry.Payload[2].ShouldBe(echo.Port);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.Proxy] - Refusal: A name allowed only by a wildcard that resolves to loopback is reported as a refused connection")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UseForwardProxy_WildcardToLoopbackName_ShouldWriteConnectionRefusal(bool tunnel)
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using TcpEchoServer echo = TcpEchoServer.Start();
        await using ProxyTestServer proxy = await StartProxyAsync(
            new ForwardProxyRule { Hosts = ["*"], Ports = [echo.Port] },
            static _ => { },
            cancellation.Token);
        long beforeConnections = WebProxyEventSource.Log.RefusedConnections;
        long beforeRequests = WebProxyEventSource.Log.RefusedRequests;

        // Act
        int status;
        if (tunnel)
        {
            await using RawProxyClient client = await RawProxyClient.ConnectAsync(proxy.Port, cancellation.Token);
            (status, _) = await client.SendAsync($"CONNECT localhost:{echo.Port} HTTP/1.1\r\nHost: localhost:{echo.Port}\r\n\r\n", cancellation.Token);
        }
        else
        {
            using HttpClient client = CreateClient(proxy);
            using HttpResponseMessage response = await client.GetAsync($"http://localhost:{echo.Port}/", cancellation.Token);
            status = (int)response.StatusCode;
        }

        // Assert — the rules allowed the request; the connect step refused every address.
        status.ShouldBe(502);
        echo.Accepted.ShouldBe(0);
        WebProxyEventSource.Log.RefusedConnections.ShouldBe(beforeConnections + 1);
        WebProxyEventSource.Log.RefusedRequests.ShouldBe(beforeRequests);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 2 && (int)e.Payload![1]! == echo.Port);
        entry.EventName.ShouldBe("ConnectionRefused");
        entry.Level.ShouldBe(EventLevel.Verbose);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "host", "port", "addressCount" });
        entry.Payload![0].ShouldBe("localhost");
        ((int)entry.Payload[2]!).ShouldBeGreaterThan(0);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Refusal: An allowed target raises no event and counts no refusal")]
    public async Task UseForwardProxy_AllowedTarget_ShouldRemainSilent()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose);
        using CancellationTokenSource cancellation = new(_testTimeout);
        await using ProxyTestServer backend = await ProxyTestServer.StartAsync(context => Task.CompletedTask, cancellation.Token);
        await using ProxyTestServer proxy = await StartProxyAsync(
            new ForwardProxyRule { Hosts = ["127.0.0.1"], Ports = [backend.Port] },
            static _ => { },
            cancellation.Token);
        using HttpClient client = CreateClient(proxy);
        long beforeRequests = WebProxyEventSource.Log.RefusedRequests;
        long beforeConnections = WebProxyEventSource.Log.RefusedConnections;

        // Act
        using HttpResponseMessage response = await client.GetAsync(backend.Uri, cancellation.Token);

        // Assert
        response.StatusCode.ShouldBe(NetHttpStatusCode.OK);
        WebProxyEventSource.Log.RefusedRequests.ShouldBe(beforeRequests);
        WebProxyEventSource.Log.RefusedConnections.ShouldBe(beforeConnections);
        recorder.Events.ShouldNotContain(e => e.EventId == 1 || e.EventId == 2);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Counters: Refusals count without listeners or allocation, and hosts are bounded")]
    public void RequestRefused_DisabledSource_ShouldCountWithoutAllocating()
    {
        // Arrange — null is deliberate: disabled diagnostics must not inspect request features.
        WebProxyEventSource source = WebProxyEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        string host = new('a', 300);
        DnsEndPoint destination = new(host, 443);
        for (int i = 0; i < 100; i++)
        {
            source.RequestRefused(null!, host, 443, addressRefused: false);
            source.ConnectionRefused(destination, 2);
        }
        long beforeRequests = source.RefusedRequests;
        long beforeConnections = source.RefusedConnections;
        long allocated = GC.GetAllocatedBytesForCurrentThread();

        // Act
        for (int i = 0; i < 1000; i++)
        {
            source.RequestRefused(null!, host, 443, addressRefused: true);
            source.ConnectionRefused(destination, 2);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

        // Assert
        allocated.ShouldBe(0);
        source.RefusedRequests.ShouldBe(beforeRequests + 1000);
        source.RefusedConnections.ShouldBe(beforeConnections + 1000);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Refusal: An oversized host is written truncated to 253 characters")]
    public void ConnectionRefused_OversizedHost_ShouldWriteBoundedHost()
    {
        // Arrange
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose);
        DnsEndPoint destination = new(new string('a', 300) + ".example", 8443);

        // Act
        WebProxyEventSource.Log.ConnectionRefused(destination, 3);

        // Assert
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == 2 && (int)e.Payload![1]! == 8443);
        entry.Payload![0].ShouldBe(new string('a', 253));
        entry.Payload[2].ShouldBe(3);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Counters: Refused requests and refused connections publish")]
    public async Task OnEventCommand_Enable_ShouldPublishCounters()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(WebProxyEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(new[] { "refused-requests", "refused-connections" }, cancellation.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static Task<ProxyTestServer> StartProxyAsync(ForwardProxyRule rule, Action<string> observeRequestId, CancellationToken cancellationToken)
    {
        return ProxyTestServer.StartAsync(
            builder => builder.Services.AddForwardProxy(proxy => proxy.Options.AddRule(rule)),
            application =>
            {
                application.Use((context, next) =>
                {
                    observeRequestId(context.Features.Get<IWebRequestIdFeature>()!.RequestId.ToHexString());
                    return next(context);
                });
                application.UseForwardProxy();
            },
            cancellationToken);
    }

    private static HttpClient CreateClient(ProxyTestServer proxy)
    {
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.Uri),
            UseProxy = true,
        });
    }
}
