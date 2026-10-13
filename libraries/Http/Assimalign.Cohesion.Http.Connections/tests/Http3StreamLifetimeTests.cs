using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>Checks request streams finish individually while their HTTP/3 connection stays reusable.</summary>
[Collection("Http3StreamLifetime")]
public class Http3StreamLifetimeTests
{
    [Theory(DisplayName = "Cohesion Test [Http.Connections] - Http3: Disposing an unanswered exchange should dispose its request stream")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_OnUnansweredExchange_ShouldDisposeRequestStream(bool peerReset)
    {
        // Arrange — the host has accepted an exchange but will not call SendAsync, for example when
        // application execution fails or the peer cancels. Only the host's exchange disposal remains.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        CompletionTrackingMultiplexedConnection? tracked = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => tracked = new CompletionTrackingMultiplexedConnection(connection));
        await using IDuplexConnection request = await peer.OpenRequestStreamAsync();
        await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("POST", "/unanswered", "https", "a"), timeout.Token);
        IHttpContext exchange = await peer.NextContextAsync();
        if (peerReset)
        {
            request.Abort(new ConnectionAbortedException("The peer cancelled its request."));
            exchange.RequestCancelled.IsCancellationRequested.ShouldBeTrue();
        }

        // Act
        await exchange.DisposeAsync();

        // Assert — the native driver's release is asynchronous; permit the disposal observer to run
        // without letting connection teardown provide the missing cleanup.
        tracked.ShouldNotBeNull();
        Stopwatch disposalWait = Stopwatch.StartNew();
        while (tracked.DisposedRequestCount != 1 && disposalWait.Elapsed < TimeSpan.FromSeconds(1))
        {
            await Task.Delay(10, timeout.Token);
        }

        tracked.DisposedRequestCount.ShouldBe(1);
        peer.Server.State.ShouldBe(ConnectionState.Open);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Sequential in-memory exchanges should complete both stream pipes")]
    public async Task SendAsync_OnSequentialInMemoryRequests_ShouldCompleteBothStreamPipes()
    {
        // Arrange — this pins existing in-memory behavior; that driver holds no accepted-stream table.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        CompletionTrackingMultiplexedConnection? tracked = null;
        await using Http3InMemoryPeer peer = await Http3InMemoryPeer.StartAsync(
            decorate: connection => tracked = new CompletionTrackingMultiplexedConnection(connection));

        for (int index = 0; index < 8; index++)
        {
            // Act — a complete request and response on the same connection, without disposing the
            // client stream before checking that the HTTP layer has completed its server-side pipes.
            await using IDuplexConnection request = await peer.OpenRequestStreamAsync();
            await request.Output.WriteAsync(HttpProtocolPayloadFactory.CreateHttp3Request("GET", $"/{index}", "https", "a"), timeout.Token);
            request.Output.Complete();
            IHttpContext exchange = await peer.NextContextAsync();
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            await peer.ConnectionContext.SendAsync(exchange, timeout.Token);
            await exchange.DisposeAsync();
            (await Http3InMemoryPeer.ReadToEndAsync(request)).ShouldNotBeEmpty();

            // Assert — output FIN was received, input was completed, and no request remains active.
            tracked.ShouldNotBeNull();
            tracked.RequestCount.ShouldBe(index + 1);
            tracked.CurrentRequests.ShouldBe(0);
            peer.Server.State.ShouldBe(ConnectionState.Open);
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http3: Sequential QUIC exchanges should release each request stream and restore the gauge")]
    public async Task SendAsync_OnSequentialQuicRequests_ShouldReleaseEachRequestStreamAndRestoreGauge()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        // Arrange — EventListener reaches the driver's real gauge and lifecycle events through its
        // diagnostics contract. No extra visibility grant or public test seam is needed.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using QuicRequestStreamEventListener events = new();
        await using Http3LoopbackServer server = await Http3LoopbackServer.StartAsync(exchange =>
        {
            exchange.Response.StatusCode = HttpStatusCode.Ok;
            exchange.Response.Body = new MemoryStream(Encoding.ASCII.GetBytes(exchange.Request.Path.Value));
            return Task.CompletedTask;
        }, timeout.Token);
        double baseline = await events.ReadNextGaugeAsync(timeout.Token);
        using HttpClient client = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

        for (int index = 0; index < 8; index++)
        {
            // Act — consume the complete response including its FIN, then let the asynchronous QUIC
            // stream release complete while the connection and its critical streams stay open.
            string path = $"/{index}";
            using HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, new Uri(server.BaseUri, path))
            {
                Version = System.Net.HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using HttpResponseMessage response = await client.SendAsync(request, timeout.Token);
            (await response.Content.ReadAsStringAsync(timeout.Token)).ShouldBe(path);
            response.Version.ShouldBe(System.Net.HttpVersion.Version30);

            Stopwatch releaseWait = Stopwatch.StartNew();
            while (events.CurrentRequestStreams != 0 && releaseWait.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(10, timeout.Token);
            }

            // Assert — each opened request has a corresponding close; the gauge contains only the
            // connection's critical streams above baseline, and a fresh sample cannot be a stale zero.
            events.RequestStreamsOpened.ShouldBe(index + 1);
            events.CurrentRequestStreams.ShouldBe(0);
            events.ConnectionCount.ShouldBe(1);
            events.CurrentCriticalStreams.ShouldBeGreaterThan(0);
            (await events.ReadNextGaugeAsync(timeout.Token)).ShouldBe(baseline + events.CurrentCriticalStreams);
            events.DuplicateCloses.ShouldBe(0);
        }
    }
}
