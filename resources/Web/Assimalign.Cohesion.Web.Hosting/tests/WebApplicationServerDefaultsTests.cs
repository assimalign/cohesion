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

        options.Interceptors.Count.ShouldBe(1);

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
