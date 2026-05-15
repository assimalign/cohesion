using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.ClientFactory.Tests;

/// <summary>
/// Lifecycle tests for <see cref="Assimalign.Cohesion.Http.HttpClientFactory"/>. Covers the
/// three substory acceptance areas:
/// </summary>
/// <list type="bullet">
///   <item><description>L01.01.11.12.01 &mdash; named clients + handler reuse within the
///   lifetime window.</description></item>
///   <item><description>L01.01.11.12.02 &mdash; handler rotation past the lifetime,
///   per-name lifetime overrides, expired-handler cleanup once GC reclaims clients.</description></item>
///   <item><description>L01.01.11.12.03 &mdash; socket-exhaustion mitigation: the factory
///   never instantiates more handlers than the rotation count under load (proven with a
///   counting handler).</description></item>
/// </list>
public class HttpClientFactoryTests
{
    [Fact]
    public void Build_WithoutNamedClients_ShouldThrow()
    {
        var builder = new HttpClientFactoryBuilder();

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void AddClient_DuplicateName_ShouldThrow()
    {
        var builder = new HttpClientFactoryBuilder()
            .AddClient("api");

        Should.Throw<ArgumentException>(() => builder.AddClient("api"));
    }

    [Fact]
    public void Create_UnknownName_ShouldThrow()
    {
        using IHttpClientFactory factory = new HttpClientFactoryBuilder()
            .AddClient("api")
            .Build();

        Should.Throw<InvalidOperationException>(() => factory.Create("unknown"));
    }

    [Fact]
    public void Create_AppliesPerNameConfiguration()
    {
        using IHttpClientFactory factory = new HttpClientFactoryBuilder()
            .AddClient("api", o =>
            {
                o.BaseAddress = new Uri("https://api.example.com");
                o.RequestTimeout = TimeSpan.FromSeconds(7);
                o.ConfigureDefaultHeaders = headers =>
                {
                    headers.Add("X-Test", "cohesion");
                };
                o.HandlerFactory = () => new StubHandler();
            })
            .Build();

        using HttpClient client = factory.Create("api");

        client.BaseAddress.ShouldBe(new Uri("https://api.example.com"));
        client.Timeout.ShouldBe(TimeSpan.FromSeconds(7));
        client.DefaultRequestHeaders.GetValues("X-Test").ShouldHaveSingleItem().ShouldBe("cohesion");
    }

    [Fact]
    public void Create_WithinLifetimeWindow_ReusesUnderlyingHandler()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int handlerCreated = 0;

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                Interlocked.Increment(ref handlerCreated);
                return new StubHandler();
            })
            .Build();

        // Three Create calls within the window should share one handler.
        using HttpClient c1 = factory.Create("api");
        using HttpClient c2 = factory.Create("api");
        clock.Advance(TimeSpan.FromMinutes(4)); // still inside the 5 min window
        using HttpClient c3 = factory.Create("api");

        handlerCreated.ShouldBe(1);
        // All three clients should be wrapping the same active inner handler.
        HttpMessageHandler? active = factory.PeekActiveInnerHandler("api");
        active.ShouldNotBeNull();
    }

    [Fact]
    public void Create_AfterLifetimeExpires_RotatesHandler()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int handlerCreated = 0;

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                Interlocked.Increment(ref handlerCreated);
                return new StubHandler();
            })
            .Build();

        using HttpClient c1 = factory.Create("api");
        HttpMessageHandler? handlerBeforeRotation = factory.PeekActiveInnerHandler("api");

        clock.Advance(TimeSpan.FromMinutes(6)); // past the 5 min window
        using HttpClient c2 = factory.Create("api");
        HttpMessageHandler? handlerAfterRotation = factory.PeekActiveInnerHandler("api");

        handlerCreated.ShouldBe(2);
        handlerAfterRotation.ShouldNotBe(handlerBeforeRotation);
    }

    [Fact]
    public void Create_AfterRotation_KeepsExpiredHandlerAliveWhileClientsHoldIt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () => new StubHandler())
            .Build();

        // Hold a client past rotation. The old handler should stay alive (expired list).
        HttpClient longLivedClient = factory.Create("api");
        try
        {
            clock.Advance(TimeSpan.FromMinutes(6));
            using HttpClient _ = factory.Create("api"); // forces rotation

            factory.CountExpiredHandlers().ShouldBe(1);

            // Even after a cleanup pass, the expired handler stays because longLivedClient
            // still holds a reference to its wrapper.
            factory.CleanupExpired();
            factory.CountExpiredHandlers().ShouldBe(1);
        }
        finally
        {
            longLivedClient.Dispose();
        }
    }

    [Fact]
    public void Create_AfterRotation_CleansUpExpiredHandlerOnceClientsCollected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        StubHandler? capturedFirstHandler = null;

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                StubHandler h = new();
                capturedFirstHandler ??= h;
                return h;
            })
            .Build();

        AllocateAndDiscardClient(factory, "api"); // client is unrooted after this returns
        clock.Advance(TimeSpan.FromMinutes(6));
        using HttpClient _ = factory.Create("api"); // rotation

        // Force GC so the wrapper around the first (now-expired) handler is reclaimable.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            int disposed = factory.CleanupExpired();
            if (disposed > 0)
            {
                break;
            }
        }

        capturedFirstHandler.ShouldNotBeNull();
        capturedFirstHandler!.IsDisposed.ShouldBeTrue();
        factory.CountExpiredHandlers().ShouldBe(0);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AllocateAndDiscardClient(IHttpClientFactory factory, string name)
    {
        // Local creates a client and lets it go out of scope so the GC can reclaim the
        // wrapping LifetimeTrackingHttpMessageHandler. Marked NoInlining so the JIT can't
        // hoist the reference out into the caller's frame.
        HttpClient client = factory.Create(name);
        client.Dispose();
    }

    [Fact]
    public void NamedClient_HandlerLifetimeOverride_TakesPrecedence()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int handlerCreated = 0;

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(30))
            .AddClient("short", o =>
            {
                o.HandlerLifetime = TimeSpan.FromSeconds(10);
                o.HandlerFactory = () =>
                {
                    Interlocked.Increment(ref handlerCreated);
                    return new StubHandler();
                };
            })
            .Build();

        using HttpClient _ = factory.Create("short");
        clock.Advance(TimeSpan.FromSeconds(15)); // past the per-name 10s limit
        using HttpClient __ = factory.Create("short");

        handlerCreated.ShouldBe(2);
    }

    [Fact]
    public async Task Create_Concurrent_NeverInstantiatesMoreHandlersThanRotations()
    {
        // L01.01.11.12.03 socket-exhaustion mitigation: a flood of concurrent Create calls
        // must NOT produce one handler per call (the naive `new HttpClient()` pattern). The
        // factory should produce exactly one handler per rotation interval.
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        int handlerCreated = 0;

        using HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                Interlocked.Increment(ref handlerCreated);
                return new StubHandler();
            })
            .Build();

        const int parallelCalls = 200;
        var tasks = new Task[parallelCalls];
        var clients = new HttpClient[parallelCalls];

        for (int i = 0; i < parallelCalls; i++)
        {
            int index = i;
            tasks[i] = Task.Run(() =>
            {
                clients[index] = factory.Create("api");
            });
        }
        await Task.WhenAll(tasks);

        try
        {
            handlerCreated.ShouldBe(1);
        }
        finally
        {
            foreach (HttpClient client in clients)
            {
                client?.Dispose();
            }
        }
    }

    [Fact]
    public void Dispose_DisposesAllPooledHandlers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var allHandlers = new List<StubHandler>();

        HttpClientFactory factory = (HttpClientFactory)new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .WithDefaultHandlerLifetime(TimeSpan.FromMinutes(5))
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                StubHandler h = new();
                allHandlers.Add(h);
                return h;
            })
            .Build();

        using HttpClient _ = factory.Create("api");
        clock.Advance(TimeSpan.FromMinutes(6));
        HttpClient longLived = factory.Create("api"); // forces a second handler into expired list
        clock.Advance(TimeSpan.FromMinutes(6));
        using HttpClient ___ = factory.Create("api"); // third handler

        allHandlers.Count.ShouldBe(3);

        longLived.Dispose();
        factory.Dispose();

        foreach (StubHandler handler in allHandlers)
        {
            handler.IsDisposed.ShouldBeTrue();
        }
    }

    [Fact]
    public void Create_AfterDispose_ShouldThrow()
    {
        IHttpClientFactory factory = new HttpClientFactoryBuilder()
            .AddClient("api")
            .Build();
        ((IDisposable)factory).Dispose();

        Should.Throw<ObjectDisposedException>(() => factory.Create("api"));
    }

    [Fact]
    public async Task DisposeAsync_DisposesPooledHandlers()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        StubHandler? captured = null;

        IHttpClientFactory factory = new HttpClientFactoryBuilder()
            .WithTimeProvider(clock)
            .AddClient("api", o => o.HandlerFactory = () =>
            {
                captured = new StubHandler();
                return captured;
            })
            .Build();

        using HttpClient _ = factory.Create("api");
        await ((IAsyncDisposable)factory).DisposeAsync();

        captured.ShouldNotBeNull();
        captured!.IsDisposed.ShouldBeTrue();
    }

    /// <summary>Counting / verifying HTTP message handler used as a stand-in for SocketsHttpHandler.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Minimal <see cref="TimeProvider"/> for deterministic rotation tests.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
