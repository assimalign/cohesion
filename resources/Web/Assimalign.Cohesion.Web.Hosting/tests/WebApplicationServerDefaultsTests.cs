using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;
using Assimalign.Cohesion.Web;

using Shouldly;

using Xunit;

using HttpVersion = Assimalign.Cohesion.Http.HttpVersion;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerDefaultsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Root AddServer overloads adapt lifecycle in registration order")]
    public async Task AddServer_WithRootOverloads_ShouldAdaptLifecycleInRegistrationOrder()
    {
        // Arrange
        List<string> events = new();
        RootApplicationServer first = new("first", events);
        RootApplicationServer second = new("second", events);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IWebApplicationContext? factoryContext = null;
        int factoryCount = 0;

        ((IWebApplicationBuilder)builder)
            .AddServer(first)
            .AddServer(context =>
            {
                factoryCount++;
                factoryContext = context;
                return second;
            });

        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();
        await ((IWebApplication)application).StopAsync();

        // Assert
        factoryCount.ShouldBe(1);
        factoryContext.ShouldBeSameAs(application.Context);
        application.Context.Servers.ToArray().ShouldBe(new IWebApplicationServer[] { first, second });
        events.ShouldBe(new[]
        {
            "first:start",
            "second:start",
            "second:stop",
            "first:stop",
        });
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Concurrent lifecycle options are rejected")]
    public void Build_WithConcurrentLifecycleOptions_ShouldRejectConfiguration()
    {
        WebApplicationOptions startOptions = new() { StartServicesConcurrently = true };
        WebApplicationOptions stopOptions = new() { StopServicesConcurrently = true };

        InvalidOperationException startException = Should.Throw<InvalidOperationException>(
            () => WebApplication.CreateBuilder(startOptions).Build());
        InvalidOperationException stopException = Should.Throw<InvalidOperationException>(
            () => WebApplication.CreateBuilder(stopOptions).Build());

        startException.Message.ShouldContain("serial", Case.Insensitive);
        stopException.Message.ShouldContain("serial", Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Root AddServer should not replace or double-start a configured default server")]
    public async Task AddServer_WithConfiguredDefaultServer_ShouldNotDoubleStartCustomServer()
    {
        // Arrange
        RootApplicationServer customServer = new("custom", new List<string>());
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(options => options.UseHttp1(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)));
        ((IWebApplicationBuilder)builder).AddServer(customServer);
        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();

        // Assert
        customServer.StartCount.ShouldBe(1);
        application.Context.Servers.Count().ShouldBe(2);
        application.Context.Servers.ShouldContain(customServer);

        await ((IWebApplication)application).StopAsync();
        customServer.StopCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: A custom-only server should not start the empty default server")]
    public async Task UseServer_WithCustomServerOnly_ShouldRunOnlyTheCustomServer()
    {
        // Arrange
        TrackingApplicationServer customServer = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Server.UseServer(customServer);
        await using WebApplication application = builder.Build();

        // Act
        await ((IWebApplication)application).StartAsync();

        // Assert
        customServer.StartCount.ShouldBe(1);
        application.Context.Servers.Single().ShouldBeSameAs(customServer);

        await ((IWebApplication)application).StopAsync();
        customServer.StopCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Should install the max-request-body-size interceptor first")]
    public void ApplyDefaultInterceptors_ShouldInstallRequestLimitsFirst()
    {
        // The web host composes the listener options with the default interceptors ahead of any
        // user configuration, so the RequestLimits interceptor occupies slot 0 — guaranteeing
        // every request carries the typed feature and later head hooks can observe it.
        HttpConnectionListenerOptions options = new();

        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);

        options.Interceptors.Count.ShouldBe(3);

        // Prove slot 0 is the RequestLimits interceptor by behavior: its head hook attaches the
        // typed feature as a write-through view over the context knob.
        HttpExchangeInterceptorRequestContext context = new()
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = 2048,
        };

        options.Interceptors[0].AfterRequestHead(context);

        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        feature.ShouldNotBeNull();
        feature!.MaxRequestBodySize.ShouldBe(2048);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Should install the HTTP/1.1 protocol-upgrade interceptor after request limits")]
    public void ApplyDefaultInterceptors_ShouldInstallProtocolUpgradeSecond()
    {
        // Decision 16 (Http ADR 1): the upgrade interceptor is on by default, so a WebSocket
        // handshake reaches context.Upgrade without any listener configuration.
        HttpConnectionListenerOptions options = new();
        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);

        HttpHeaderCollection requestHeaders = new();
        requestHeaders[HttpHeaderKey.Connection] = "Upgrade";
        requestHeaders[HttpHeaderKey.Upgrade] = "websocket";
        HttpFeatureCollection features = new();

        // Prove slot 1 is the upgrade interceptor by behavior: its head hook records the upgrade
        // signal and its response hook installs the feature context.Upgrade reads.
        options.Interceptors[1].AfterRequestHead(new HttpExchangeInterceptorRequestContext
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Get,
            Path = new HttpPath("/socket"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = requestHeaders.AsReadOnly(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        });
        options.Interceptors[1].BeforeResponse(new HttpExchangeInterceptorResponseContext
        {
            Version = HttpVersion.Http11,
            Headers = new HttpHeaderCollection(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            ResponseBody = System.IO.Stream.Null,
            Control = new TakeoverOnlyControl(),
        });

        IHttpProtocolUpgrade? upgrade = features.Get<IHttpProtocolUpgradeFeature>()?.Upgrade;
        upgrade.ShouldNotBeNull();
        upgrade!.Kind.ShouldBe(HttpProtocolUpgradeKind.Upgrade);
        upgrade.Protocol.ShouldBe("websocket");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Should install the extended CONNECT interceptor after the protocol-upgrade interceptor")]
    public async Task ApplyDefaultInterceptors_ShouldInstallExtendedConnectThird()
    {
        // Arrange — #1368: the extended CONNECT feature is installed by an interceptor, so a WebSocket
        // over HTTP/2 or HTTP/3 reaches context.ExtendedConnect only because the host registers it.
        HttpConnectionListenerOptions options = new();
        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);
        HttpFeatureCollection features = new();
        TunnelOnlyControl control = new();

        // Act — prove slot 2 is the extended CONNECT interceptor by behavior: its head hook installs the
        // feature for a validated :protocol, and its response hook binds it to the exchange control.
        options.Interceptors[2].AfterRequestHead(new HttpExchangeInterceptorRequestContext
        {
            Version = HttpVersion.Http20,
            Method = HttpMethod.Connect,
            Path = new HttpPath("/socket"),
            Scheme = HttpScheme.Https,
            Host = new HttpHost("api.test"),
            Protocol = "websocket",
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = null,
        });
        options.Interceptors[2].BeforeResponse(new HttpExchangeInterceptorResponseContext
        {
            Version = HttpVersion.Http20,
            Headers = new HttpHeaderCollection(),
            Features = features,
            ConnectionInfo = HttpConnectionInfo.Empty,
            ResponseBody = System.IO.Stream.Null,
            Control = control,
        });

        IHttpExtendedConnectFeature? extendedConnect = features.Get<IHttpExtendedConnectFeature>();
        System.IO.Stream? tunnel = extendedConnect is null ? null : await extendedConnect.AcceptAsync();

        // Assert
        extendedConnect.ShouldNotBeNull();
        extendedConnect!.Protocol.ShouldBe("websocket");
        tunnel.ShouldBeSameAs(System.IO.Stream.Null);
        control.AcceptCount.ShouldBe(1);
    }

    /// <summary>An exchange control whose only capability is a takeover that was never exercised.</summary>
    private sealed class TakeoverOnlyControl : IHttpExchangeControl
    {
        public bool HasResponseStarted => false;

        public bool CanWriteInterimResponse => false;

        public ValueTask WriteInterimResponseAsync(Assimalign.Cohesion.Http.HttpStatusCode statusCode, IHttpHeaderCollection? headers = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool CanTakeOver => true;

        public System.IO.Stream TakeOver() => throw new NotSupportedException();

        public bool CanAcceptTunnel => false;

        public ValueTask<System.IO.Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>An exchange control whose only capability is an extended CONNECT tunnel accept, counted.</summary>
    private sealed class TunnelOnlyControl : IHttpExchangeControl
    {
        public int AcceptCount { get; private set; }

        public bool HasResponseStarted => false;

        public bool CanWriteInterimResponse => false;

        public ValueTask WriteInterimResponseAsync(Assimalign.Cohesion.Http.HttpStatusCode statusCode, IHttpHeaderCollection? headers = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public bool CanTakeOver => false;

        public System.IO.Stream TakeOver() => throw new NotSupportedException();

        public bool CanAcceptTunnel => AcceptCount == 0;

        public ValueTask<System.IO.Stream> AcceptTunnelAsync(CancellationToken cancellationToken = default)
        {
            AcceptCount++;
            return ValueTask.FromResult(System.IO.Stream.Null);
        }
    }

    private sealed class TrackingApplicationServer : IWebApplicationServer, IHostService
    {
        public ServiceId Id { get; } = ServiceId.New();

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RootApplicationServer : IWebApplicationServer
    {
        private readonly string _name;
        private readonly ICollection<string> _events;

        /// <summary>
        /// Initializes a new instance of the <see cref="RootApplicationServer"/> class.
        /// </summary>
        /// <param name="name">The name that prefixes each recorded lifecycle event.</param>
        /// <param name="events">The collection that receives the recorded lifecycle events.</param>
        public RootApplicationServer(
            string name,
            ICollection<string> events)
        {
            _name = name;
            _events = events;
        }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            _events.Add($"{_name}:start");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            _events.Add($"{_name}:stop");
            return Task.CompletedTask;
        }
    }
}
