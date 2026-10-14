using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.WebSockets.Internal;
using Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

using Shouldly;

using Xunit;

using HttpMethod = Assimalign.Cohesion.Http.HttpMethod;

namespace Assimalign.Cohesion.Web.WebSockets.Tests;

/// <summary>
/// <c>UseWebSockets</c> driven through its public verb over context doubles: what passes, what is
/// refused and with which status, and what an accept downstream receives.
/// </summary>
public class WebSocketMiddlewareTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: An ordinary request passes through untouched")]
    public async Task UseWebSockets_OrdinaryRequest_PassesThrough()
    {
        // Arrange
        PolicyTestContext context = new(HttpMethod.Get);
        context.Request.Headers[HttpHeaderKey.Origin] = "https://elsewhere.example";

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        context.Features.Get<IHttpWebSocketFeature>().ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A malformed handshake is refused with 400")]
    public async Task UseWebSockets_MalformedHandshake_Refuses400()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketKey] = "not-a-key";

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: Another WebSocket version is refused with 426 and Sec-WebSocket-Version: 13")]
    public async Task UseWebSockets_UnsupportedVersion_Refuses426()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketVersion] = "8";

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.UpgradeRequired);
        context.ResponseHeader(HttpHeaderKey.SecWebSocketVersion).ShouldBe("13");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A cross-site handshake is refused with 403")]
    public async Task UseWebSockets_CrossOriginHandshake_Refuses403()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "https://attacker.example");

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        context.Upgrade!.AcceptCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A same-origin handshake passes, however the origin is written")]
    [InlineData("https://app.example")]
    [InlineData("https://APP.example:443")]
    public async Task UseWebSockets_SameOriginHandshake_Passes(string origin)
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: origin, scheme: HttpScheme.Https, host: "app.example");

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: The same scheme on another port is another origin")]
    public async Task UseWebSockets_SameHostOtherPort_Refuses403()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "https://app.example:8443", scheme: HttpScheme.Https, host: "app.example");

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: Behind a TLS-terminating proxy the same-origin check uses the effective scheme and host")]
    public async Task UseWebSockets_SameOriginBehindProxy_Passes()
    {
        // Arrange — the server sees http://10.0.0.5:8080; the page is https://app.example.
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "https://app.example", scheme: HttpScheme.Http, host: "10.0.0.5:8080");
        context.Features.Set(new FakeForwardedFeature(HttpScheme.Https, "app.example", HttpScheme.Http, "10.0.0.5:8080"));

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: An allowed origin passes, compared in its serialized form")]
    public async Task UseWebSockets_AllowedOrigin_Passes()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "https://chat.example");

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, options => options.AllowedOrigins.Add("HTTPS://Chat.Example:443"));

        // Assert
        continued.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A handshake without Origin, which no browser sends, passes")]
    public async Task UseWebSockets_NoOrigin_Passes()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: null);

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: The opaque origin and a malformed one are refused")]
    [InlineData("null")]
    [InlineData("https://app.example/page")]
    public async Task UseWebSockets_OpaqueOrMalformedOrigin_Refuses403(string origin)
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: origin);

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: Several Origin fields are refused")]
    public async Task UseWebSockets_SeveralOriginFields_Refuses403()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.Origin] = new HttpHeaderValue(new[] { "https://app.example", "https://attacker.example" });

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null);

        // Assert
        continued.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: AllowAnyOrigin turns the hijacking defense off")]
    public async Task UseWebSockets_AllowAnyOrigin_PassesCrossOriginHandshake()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake(origin: "null");

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, options => options.AllowAnyOrigin = true);

        // Assert
        continued.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: The accept downstream goes through the policy, and the handshake stays the exchange's")]
    public async Task UseWebSockets_ValidHandshake_InstallsThePolicyOverTheExchangeFeature()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        IHttpWebSocketFeature inner = context.WebSockets;
        IHttpWebSocketFeature? seen = null;

        // Act
        bool continued = await TestPipelineBuilder.RunAsync(context, configure: null, downstream: ctx =>
        {
            seen = ctx.WebSockets;
            return Task.CompletedTask;
        });

        // Assert
        continued.ShouldBeTrue();
        seen.ShouldBeOfType<WebSocketPolicyFeature>();
        seen.Name.ShouldBe(inner.Name);
        seen.HandshakeStatus.ShouldBe(HttpWebSocketHandshakeStatus.Valid);
        seen.IsWebSocketRequest.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: An accept that leaves compression unset takes the policy's opt-in")]
    public async Task UseWebSockets_CompressionEnabledByPolicy_NegotiatesForADefaultAccept()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        context.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate; client_max_window_bits";

        // Act
        await TestPipelineBuilder.RunAsync(context, options => options.DangerousEnableCompression = true, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
        });

        // Assert
        context.Upgrade!.AcceptedHeaders["Sec-WebSocket-Extensions"].ShouldBe("permessage-deflate");
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: Compression stays off unless enabled, and an accept can turn the policy's opt-in off")]
    public async Task UseWebSockets_CompressionNotEnabledOrOverridden_IsDeclined()
    {
        // Arrange
        PolicyTestContext byDefault = PolicyTestContext.CreateHandshake();
        byDefault.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate";
        PolicyTestContext overridden = PolicyTestContext.CreateHandshake();
        overridden.Request.Headers[HttpHeaderKey.SecWebSocketExtensions] = "permessage-deflate";

        // Act
        await TestPipelineBuilder.RunAsync(byDefault, configure: null, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync();
        });
        await TestPipelineBuilder.RunAsync(overridden, options => options.DangerousEnableCompression = true, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync(new HttpWebSocketAcceptOptions { DangerousEnableCompression = false });
        });

        // Assert
        byDefault.Upgrade!.AcceptedHeaders.ContainsKey("Sec-WebSocket-Extensions").ShouldBeFalse();
        overridden.Upgrade!.AcceptedHeaders.ContainsKey("Sec-WebSocket-Extensions").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: The policy's defaults never change the accept's own options")]
    public void Apply_CallerOptions_AreNotModified()
    {
        // Arrange
        WebSocketPolicy policy = WebSocketPolicy.Create(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(5),
            KeepAliveTimeout = TimeSpan.FromSeconds(7),
            DangerousEnableCompression = true,
        });
        HttpWebSocketAcceptOptions caller = new() { SubProtocol = "chat", KeepAliveInterval = TimeSpan.FromSeconds(1), ServerMaxWindowBits = 12 };

        // Act
        HttpWebSocketAcceptOptions effective = policy.Apply(caller);
        HttpWebSocketAcceptOptions defaults = policy.Apply(null);

        // Assert
        effective.ShouldNotBeSameAs(caller);
        effective.SubProtocol.ShouldBe("chat");
        effective.KeepAliveInterval.ShouldBe(TimeSpan.FromSeconds(1));
        effective.KeepAliveTimeout.ShouldBe(TimeSpan.FromSeconds(7));
        effective.DangerousEnableCompression.ShouldBe(true);
        effective.ServerMaxWindowBits.ShouldBe(12);
        caller.KeepAliveTimeout.ShouldBeNull();
        caller.DangerousEnableCompression.ShouldBeNull();
        defaults.KeepAliveInterval.ShouldBe(TimeSpan.FromSeconds(5));
        defaults.ServerMaxWindowBits.ShouldBe(15);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: Without the server's drain signal the accept returns the socket as accepted")]
    public async Task UseWebSockets_WithoutDrainFeature_ReturnsTheAcceptedSocket()
    {
        // Arrange
        PolicyTestContext context = PolicyTestContext.CreateHandshake();
        WebSocket? accepted = null;

        // Act
        await TestPipelineBuilder.RunAsync(context, configure: null, downstream: async ctx =>
        {
            accepted = await ctx.WebSockets.AcceptWebSocketAsync();
        });

        // Assert
        accepted.ShouldNotBeNull();
        accepted.ShouldNotBeOfType<DrainAwareWebSocket>();
        accepted.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: When the server drains, an open socket is closed with 1001 and the application's close completes")]
    public async Task UseWebSockets_ServerDrains_ClosesTheSocketWith1001()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        using FakeDrainFeature drain = new();
        PolicyTestContext context = PolicyTestContext.CreateHandshake(transport: pair.Server);
        context.Features.Set(drain);
        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });

        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WebSocketCloseStatus? serverSaw = null;

        Task pipeline = TestPipelineBuilder.RunAsync(context, configure: null, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellationToken);
            accepted.TrySetResult();

            // The receive loop every echo endpoint has, ending with CloseAsync.
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
            serverSaw = result.CloseStatus;
            await socket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, cancellationToken);
        });

        await accepted.Task.WaitAsync(cancellationToken);

        // Act
        drain.BeginDrain();
        WebSocketReceiveResult received = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
        await client.CloseOutputAsync(received.CloseStatus!.Value, received.CloseStatusDescription, cancellationToken);

        // Assert — the application's own CloseAsync after the drain's close did not throw.
        await pipeline.WaitAsync(cancellationToken);
        received.MessageType.ShouldBe(WebSocketMessageType.Close);
        received.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
        serverSaw.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A socket accepted after the drain began is closed with 1001 at once")]
    public async Task UseWebSockets_AcceptAfterDrainBegan_ClosesAtOnce()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        using FakeDrainFeature drain = new();
        drain.BeginDrain();
        PolicyTestContext context = PolicyTestContext.CreateHandshake(transport: pair.Server);
        context.Features.Set(drain);
        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });

        // Act
        Task pipeline = TestPipelineBuilder.RunAsync(context, configure: null, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellationToken);
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
        });

        WebSocketReceiveResult received = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
        await client.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, cancellationToken);

        // Assert — the application's CloseOutputAsync after the drain's close did not throw either.
        await pipeline.WaitAsync(cancellationToken);
        received.CloseStatus.ShouldBe(WebSocketCloseStatus.EndpointUnavailable);
    }

    [Fact(DisplayName = "Cohesion Test [Web.WebSockets] - UseWebSockets: A socket the application already closed is left alone by the drain")]
    public async Task UseWebSockets_ApplicationClosedFirst_DrainSendsNothing()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_timeout);
        CancellationToken cancellationToken = cancellation.Token;
        await using LoopbackStreamPair pair = await LoopbackStreamPair.CreateAsync(cancellationToken);
        using FakeDrainFeature drain = new();
        PolicyTestContext context = PolicyTestContext.CreateHandshake(transport: pair.Server);
        context.Features.Set(drain);
        using WebSocket client = WebSocket.CreateFromStream(pair.Client, new WebSocketCreationOptions { IsServer = false });

        TaskCompletionSource closeSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        WebSocketState stateAfterDrain = WebSocketState.None;

        Task pipeline = TestPipelineBuilder.RunAsync(context, configure: null, downstream: async ctx =>
        {
            using WebSocket socket = await ctx.WebSockets.AcceptWebSocketAsync(cancellationToken: cancellationToken);
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "bye", cancellationToken);
            closeSent.TrySetResult();
            await drained.Task.WaitAsync(cancellationToken);
            stateAfterDrain = socket.State;
            await socket.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
        });

        await closeSent.Task.WaitAsync(cancellationToken);

        // Act
        drain.BeginDrain();
        drained.TrySetResult();
        WebSocketReceiveResult received = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);

        // Assert — the peer saw the application's close, not a second one from the drain.
        await pipeline.WaitAsync(cancellationToken);
        received.CloseStatus.ShouldBe(WebSocketCloseStatus.PolicyViolation);
        stateAfterDrain.ShouldBe(WebSocketState.CloseSent);
    }
}
