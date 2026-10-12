using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.WebSockets.Internal;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

[Collection("WebSocket events")]
public sealed class WebSocketEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - EventSource: Name matches the assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(WebSocketEventSource)).ShouldBe("Assimalign.Cohesion.Web.WebSockets");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only strict manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only strict manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(WebSocketEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - Lifecycle: A socket with or without drain reports once and restores the gauge")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_AcceptedSocket_ReportsOnceAndRestoresGauge(bool withDrain)
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        CancellationToken cancellationToken = timeout.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        using FakeDrainFeature drain = new();
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake(transport: pair.Server);
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        context.Request.Headers[HttpHeaderKey.SecWebSocketProtocol] = "chat";
        if (withDrain)
        {
            context.Features.Set(drain);
        }

        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });
        WebSocketEventSource source = WebSocketEventSource.Log;
        long currentBefore = source.CurrentWebSockets;
        long totalBefore = source.TotalWebSockets;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using Activity ambient = new Activity("unrelated").SetIdFormat(ActivityIdFormat.W3C).Start();

        // Act
        Task pipeline = TestPipelineBuilder.RunAsync(context, configure: null, downstream: async exchange =>
        {
            using WebSocket socket = await exchange.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "chat" }, cancellationToken);
            source.CurrentWebSockets.ShouldBe(currentBefore + 1);
            accepted.TrySetResult();
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
            await socket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        });
        await accepted.Task.WaitAsync(cancellationToken);
        if (withDrain)
        {
            drain.BeginDrain();
            WebSocketReceiveResult close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
            await client.CloseOutputAsync(close.CloseStatus!.Value, null, cancellationToken);
        }
        else
        {
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
            await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
        }

        await pipeline.WaitAsync(cancellationToken);
        WebSocketPolicyFeature feature = context.WebSockets.ShouldBeOfType<WebSocketPolicyFeature>();
        feature.Release();
        feature.Release();

        // Assert
        source.CurrentWebSockets.ShouldBe(currentBefore);
        source.TotalWebSockets.ShouldBe(totalBefore + 1);
        string requestId = identity.Id.ToHexString();
        EventWrittenEventArgs opened = recorder.Events.Single(entry => entry.EventId == 2 && HasRequestId(entry, requestId));
        opened.Level.ShouldBe(EventLevel.Verbose);
        opened.PayloadNames!.ToArray().ShouldBe(new[] { "requestId", "subProtocol" });
        opened.Payload!.ToArray().ShouldBe(new object[] { requestId, "chat" });
        EventWrittenEventArgs closed = recorder.Events.Single(entry => entry.EventId == 3 && HasRequestId(entry, requestId));
        closed.Level.ShouldBe(EventLevel.Verbose);
        closed.PayloadNames!.ToArray().ShouldBe(new[] { "requestId", "closeStatus", "closedByDrain" });
        closed.Payload!.ToArray().ShouldBe(new object[] { requestId, withDrain ? 1001 : 1000, withDrain });
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Lifecycle: An aborted socket and a throwing handler release once")]
    public async Task Lifecycle_HandlerThrowsAfterAbort_ReportsCloseOnce()
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake();
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        WebSocketEventSource source = WebSocketEventSource.Log;
        long currentBefore = source.CurrentWebSockets;
        long totalBefore = source.TotalWebSockets;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        InvalidOperationException failure = new("handler failure");

        // Act
        InvalidOperationException observed = await Should.ThrowAsync<InvalidOperationException>(() =>
            TestPipelineBuilder.RunAsync(context, configure: null, downstream: async exchange =>
            {
                using WebSocket socket = await exchange.WebSockets.AcceptWebSocketAsync(cancellationToken: CancellationToken.None);
                socket.Abort();
                throw failure;
            }));
        context.WebSockets.ShouldBeOfType<WebSocketPolicyFeature>().Release();

        // Assert
        observed.ShouldBeSameAs(failure);
        source.CurrentWebSockets.ShouldBe(currentBefore);
        source.TotalWebSockets.ShouldBe(totalBefore + 1);
        EventWrittenEventArgs closed = recorder.Events.Single(entry => entry.EventId == 3 && HasRequestId(entry, identity.Id.ToHexString()));
        closed.Payload![1].ShouldBe(-1);
        closed.Payload![2].ShouldBe(false);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - Lifecycle: A failed accept never changes the socket counters")]
    public async Task AcceptWebSocketAsync_FailedAccept_DoesNotChangeSocketCounters()
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake();
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        WebSocketEventSource source = WebSocketEventSource.Log;
        long currentBefore = source.CurrentWebSockets;
        long totalBefore = source.TotalWebSockets;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);

        // Act
        await Should.ThrowAsync<ArgumentException>(() => TestPipelineBuilder.RunAsync(context, configure: null, downstream: async exchange =>
        {
            await exchange.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { SubProtocol = "unoffered" }, CancellationToken.None);
        }));

        // Assert
        source.CurrentWebSockets.ShouldBe(currentBefore);
        source.TotalWebSockets.ShouldBe(totalBefore);
        recorder.Events.ShouldNotContain(entry => HasRequestId(entry, identity.Id.ToHexString()));
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - Lifecycle: Nested policies count one underlying socket and retain drain ownership")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_NestedPolicies_CountsOneSocket(bool withDrain)
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake();
        using FakeDrainFeature drain = new();
        if (withDrain)
        {
            context.Features.Set(drain);
        }

        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        WebSocketEventSource source = WebSocketEventSource.Log;
        long currentBefore = source.CurrentWebSockets;
        long totalBefore = source.TotalWebSockets;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        TestPipelineBuilder builder = new();
        builder.UseWebSockets();
        builder.UseWebSockets();
        builder.Use(_ => async exchange =>
        {
            using WebSocket socket = await exchange.WebSockets.AcceptWebSocketAsync(cancellationToken: CancellationToken.None);
            source.CurrentWebSockets.ShouldBe(currentBefore + 1);
            if (withDrain)
            {
                drain.BeginDrain();
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        });

        // Act
        await builder.Build().ExecuteAsync(context, CancellationToken.None);

        // Assert
        source.CurrentWebSockets.ShouldBe(currentBefore);
        source.TotalWebSockets.ShouldBe(totalBefore + 1);
        recorder.Events.Count(entry => entry.EventId == 2 && HasRequestId(entry, identity.Id.ToHexString())).ShouldBe(1);
        EventWrittenEventArgs closed = recorder.Events.Single(entry => entry.EventId == 3 && HasRequestId(entry, identity.Id.ToHexString()));
        closed.Payload![2].ShouldBe(withDrain);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - HandshakeRejected: Only a disallowed normalized origin is written")]
    [InlineData("HTTPS://ATTACKER.Example:443", false, "OriginNotAllowed", "https://attacker.example")]
    [InlineData("https://attacker.example/private?token=secret", false, "OriginUnparseable", "")]
    [InlineData("https://first.example", true, "OriginMultiple", "")]
    public async Task HandshakeRejected_OriginRefused_WritesOnlyNormalizedDisallowedOrigin(string origin, bool multiple, string reason, string expectedOrigin)
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: origin);
        if (multiple)
        {
            context.Request.Headers[HttpHeaderKey.Origin] = new HttpHeaderValue(new[] { origin, "https://second.example" });
        }

        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        context.Features.Set(new DiagnosticEndpointFeature());
        WebSocketEventSource source = WebSocketEventSource.Log;
        long rejectedBefore = source.RejectedHandshakes;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        source.RejectedHandshakes.ShouldBe(rejectedBefore + 1);
        EventWrittenEventArgs rejected = recorder.Events.Single(entry => entry.EventId == 1 && HasRequestId(entry, identity.Id.ToHexString()));
        rejected.Level.ShouldBe(EventLevel.Verbose);
        rejected.PayloadNames!.ToArray().ShouldBe(new[] { "reason", "origin", "routeTemplate", "requestId" });
        rejected.Payload!.ToArray().ShouldBe(new object[] { reason, expectedOrigin, "/chat/{room}", identity.Id.ToHexString() });
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - HandshakeRejected: A long normalized origin is bounded")]
    public async Task HandshakeRejected_LongOrigin_TruncatesTo256Characters()
    {
        // Arrange
        string origin = "https://" + new string('a', 300) + ".example";
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: origin);
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        using EventSourceRecorder recorder = new(WebSocketEventSource.Log, EventLevel.Verbose);

        // Act
        await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        EventWrittenEventArgs rejected = recorder.Events.Single(entry => entry.EventId == 1 && HasRequestId(entry, identity.Id.ToHexString()));
        rejected.Payload![1].ShouldBe(origin[..256]);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - HandshakeRejected: Protocol refusals write status without origin")]
    [InlineData(false, "Invalid")]
    [InlineData(true, "UnsupportedVersion")]
    public async Task HandshakeRejected_InvalidProtocol_WritesStatusWithoutOrigin(bool unsupportedVersion, string reason)
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "https://private.example");
        context.Request.Headers[unsupportedVersion ? HttpHeaderKey.SecWebSocketVersion : HttpHeaderKey.SecWebSocketKey] = "invalid";
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        WebSocketEventSource source = WebSocketEventSource.Log;
        long rejectedBefore = source.RejectedHandshakes;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);

        // Act
        await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        source.RejectedHandshakes.ShouldBe(rejectedBefore + 1);
        EventWrittenEventArgs rejected = recorder.Events.Single(entry => entry.EventId == 1 && HasRequestId(entry, identity.Id.ToHexString()));
        rejected.Payload![0].ShouldBe(reason);
        rejected.Payload![1].ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - MapWebSocket: A plain request reports the endpoint rejection")]
    public async Task InvokeAsync_PlainRequestAtSocketEndpoint_ReportsRejectedHandshake()
    {
        // Arrange
        await using PolicyTestContext context = new(HttpMethod.Get);
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        WebSocketEndpoint endpoint = new((_, _) => Task.CompletedTask, null);
        WebSocketEventSource source = WebSocketEventSource.Log;
        long rejectedBefore = source.RejectedHandshakes;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);

        // Act
        await endpoint.InvokeAsync(context);

        // Assert
        source.RejectedHandshakes.ShouldBe(rejectedBefore + 1);
        EventWrittenEventArgs rejected = recorder.Events.Single(entry => entry.EventId == 1 && HasRequestId(entry, identity.Id.ToHexString()));
        rejected.Payload![0].ShouldBe("None");
        rejected.Payload![1].ShouldBe(string.Empty);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - DrainCloseFailed: An already disposed socket reports once at Verbose")]
    public async Task DrainCloseFailed_DisposedSocket_ReportsVerboseFailure()
    {
        // Arrange
        await using PolicyTestContext context = PolicyTestContext.CreateHandshake();
        DiagnosticRequestIdFeature identity = new();
        context.Features.Set(identity);
        using FakeDrainFeature drain = new();
        using WebSocket inner = WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions { IsServer = true });
        using DrainAwareWebSocket socket = new(context, inner, drain.Draining);
        inner.Dispose();
        using EventSourceRecorder recorder = new(WebSocketEventSource.Log, EventLevel.Verbose);

        // Act — the application also waits for the failed drain attempt without a second close.
        drain.BeginDrain();
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        // Assert
        socket.ClosedByDrain.ShouldBeTrue();
        EventWrittenEventArgs failed = recorder.Events.Single(entry => entry.EventId == 4 && HasRequestId(entry, identity.Id.ToHexString()));
        failed.Level.ShouldBe(EventLevel.Verbose);
        failed.PayloadNames!.ToArray().ShouldBe(new[] { "requestId", "exceptionType", "exceptionMessage" });
        failed.Payload![1].ShouldBe(typeof(WebSocketException).FullName);
        failed.Payload![2].ShouldBeOfType<string>().ShouldNotBeEmpty();
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0 || entry.Level == EventLevel.Warning);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - EventSource: Disabled diagnostics keep exact counters without reading correlation")]
    public async Task Lifecycle_NoListener_CountsWithoutReadingCorrelation()
    {
        // Arrange
        WebSocketEventSource source = WebSocketEventSource.Log;
        source.IsEnabled().ShouldBeFalse();
        long currentBefore = source.CurrentWebSockets;
        long totalBefore = source.TotalWebSockets;
        long rejectedBefore = source.RejectedHandshakes;
        await using PolicyTestContext accepted = PolicyTestContext.CreateHandshake();
        await using PolicyTestContext rejected = PolicyTestContext.CreateHandshake(origin: "https://elsewhere.example");
        DiagnosticRequestIdFeature identity = new();
        DiagnosticEndpointFeature endpoint = new();
        accepted.Features.Set(identity);
        rejected.Features.Set(identity);
        rejected.Features.Set(endpoint);

        // Act
        await TestPipelineBuilder.RunAsync(accepted, configure: null, downstream: async exchange =>
        {
            using WebSocket socket = await exchange.WebSockets.AcceptWebSocketAsync(cancellationToken: CancellationToken.None);
            source.CurrentWebSockets.ShouldBe(currentBefore + 1);
        });
        await TestPipelineBuilder.RunAsync(rejected, configure: null);

        // Assert
        source.CurrentWebSockets.ShouldBe(currentBefore);
        source.TotalWebSockets.ShouldBe(totalBefore + 1);
        source.RejectedHandshakes.ShouldBe(rejectedBefore + 1);
        identity.ReadCount.ShouldBe(0);
        endpoint.ReadCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - EventSource: Every counter publishes")]
    public async Task EnableCounters_Source_PublishesEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(WebSocketEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["current-websockets", "total-websockets", "rejected-handshakes"], timeout.Token);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
    }

    private static bool HasRequestId(EventWrittenEventArgs entry, string requestId)
        => entry.PayloadNames is not null && entry.Payload is not null
            && entry.PayloadNames.IndexOf("requestId") is int index && index >= 0
            && Equals(entry.Payload[index], requestId);
}
