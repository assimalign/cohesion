using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;
using Assimalign.Cohesion.Web.Proxy.Internal;
using Assimalign.Cohesion.Web.Testing;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Proxy.Tests;

/// <summary>
/// How the proxies compose: both registration overloads of each verb register its setting, the
/// container disposes the connection pools the settings own, and each <c>Use</c> verb fails composition
/// with a message naming its <c>Add</c> verb when that was not called.
/// </summary>
public class ProxyCompositionTests
{
    private static readonly Uri _destination = new("http://backend/");

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddReverseProxy registers the reverse-proxy setting the application lists")]
    public async Task AddReverseProxy_WithCallback_ShouldRegisterTheSetting()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddReverseProxy(proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [_destination] }));

        // Assert
        IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>> setting = factory.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>>>()
            .ShouldHaveSingleItem();
        setting.Name.ShouldBe("reverse-proxy");
        setting.Value.ShouldHaveSingleItem().Name.ShouldBe("api");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddReverseProxy without a callback registers an empty route list")]
    public async Task AddReverseProxy_Bare_ShouldRegisterAnEmptyRouteList()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddReverseProxy();

        // Assert
        IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>> setting = factory.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>>>()
            .ShouldHaveSingleItem();
        setting.Value.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddReverseProxy with the service provider builds the setting when it is resolved")]
    public async Task AddReverseProxy_WithServiceProvider_ShouldRegisterTheSetting()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        IServiceProvider? seen = null;

        // Act
        factory.Builder.Services.AddReverseProxy((services, proxy) =>
        {
            seen = services;
            proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [_destination] });
        });

        // Assert
        seen.ShouldBeNull();
        ReverseProxySetting setting = ReverseProxySetting.Find(factory.Application.Context).ShouldNotBeNull();
        setting.Value.ShouldHaveSingleItem().Name.ShouldBe("api");
        seen.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: The reverse-proxy setting registered last is the one the proxy uses")]
    public async Task AddReverseProxy_Twice_ShouldUseTheLastRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddReverseProxy(proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "first", Destinations = [_destination] }));
        factory.Builder.Services.AddReverseProxy(proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "second", Destinations = [_destination] }));

        // Assert
        ReverseProxySetting.Find(factory.Application.Context).ShouldNotBeNull().Value.ShouldHaveSingleItem().Name.ShouldBe("second");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddForwardProxy registers the forward-proxy setting the application lists")]
    public async Task AddForwardProxy_WithCallback_ShouldRegisterTheSetting()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddForwardProxy(proxy => proxy.Options.AddRule(new ForwardProxyRule { Hosts = ["*.example.com"] }));

        // Assert
        IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>> setting = factory.Application.Context.Settings
            .OfType<IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>>>()
            .ShouldHaveSingleItem();
        setting.Name.ShouldBe("forward-proxy");
        setting.Value.ShouldHaveSingleItem().Hosts.ShouldBe(["*.example.com"]);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddForwardProxy without a callback registers a proxy that allows nothing")]
    public async Task AddForwardProxy_Bare_ShouldRegisterAnEmptyRuleList()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        factory.Builder.Services.AddForwardProxy();

        // Assert
        ForwardProxySetting.Find(factory.Application.Context).ShouldNotBeNull().Value.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: AddForwardProxy with the service provider builds the setting when it is resolved")]
    public async Task AddForwardProxy_WithServiceProvider_ShouldRegisterTheSetting()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();
        IServiceProvider? seen = null;

        // Act
        factory.Builder.Services.AddForwardProxy((services, proxy) =>
        {
            seen = services;
            proxy.Options.AddRule(new ForwardProxyRule { Hosts = ["intranet.test"], Ports = [8080] });
        });

        // Assert
        seen.ShouldBeNull();
        ForwardProxySetting setting = ForwardProxySetting.Find(factory.Application.Context).ShouldNotBeNull();
        setting.Value.ShouldHaveSingleItem().Ports.ShouldBe([8080]);
        seen.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: Invalid options fail the registration, not the first request")]
    public async Task AddReverseProxy_InvalidRoute_ShouldThrowAtRegistration()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act / Assert
        Should.Throw<ArgumentException>(() => factory.Builder.Services.AddReverseProxy(
            proxy => proxy.Options.AddRoute(new ReverseProxyRoute { Name = "api", Destinations = [new Uri("ftp://backend/")] })));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: UseReverseProxy without AddReverseProxy fails to start, naming the verb")]
    public async Task UseReverseProxy_WithoutRegistration_ShouldFailToStart()
    {
        // Arrange — a reverse proxy has no safe default route.
        await using WebApplicationTestFactory factory = new();
        WebApplication app = factory.Application;
        app.UseReverseProxy();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IWebApplication)app).StartAsync());

        // Assert
        exception.Message.ShouldContain("AddReverseProxy", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: UseForwardProxy without AddForwardProxy fails to start, naming the verb")]
    public async Task UseForwardProxy_WithoutRegistration_ShouldFailToStart()
    {
        // Arrange — a forward proxy has no safe default rule.
        await using WebApplicationTestFactory factory = new();
        WebApplication app = factory.Application;
        app.UseForwardProxy();

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => ((IWebApplication)app).StartAsync());

        // Assert
        exception.Message.ShouldContain("AddForwardProxy", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: Disposing the application disposes the reverse proxy's connection pool")]
    public async Task DisposeAsync_ReverseProxy_ShouldDisposeTheConnectionPool()
    {
        // Arrange
        SocketsHttpHandler? handler = null;
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddReverseProxy(proxy => proxy.Options.ConfigureHandler = configured => handler = configured);
        _ = ReverseProxySetting.Find(factory.Application.Context).ShouldNotBeNull();

        // Act — a handler that was neither started nor disposed takes a property; a disposed one throws.
        handler.ShouldNotBeNull().ConnectTimeout = TimeSpan.FromSeconds(5);
        await factory.DisposeAsync();

        // Assert
        Should.Throw<ObjectDisposedException>(() => handler.ConnectTimeout = TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Proxy] - Composition: Disposing the application disposes both of the forward proxy's connection pools")]
    public async Task DisposeAsync_ForwardProxy_ShouldDisposeBothConnectionPools()
    {
        // Arrange
        List<SocketsHttpHandler> handlers = new();
        WebApplicationTestFactory factory = new();
        factory.Builder.Services.AddForwardProxy(proxy => proxy.Options.ConfigureHandler = handlers.Add);
        _ = ForwardProxySetting.Find(factory.Application.Context).ShouldNotBeNull();

        // Act
        await factory.DisposeAsync();

        // Assert
        handlers.Count.ShouldBe(2);
        foreach (SocketsHttpHandler handler in handlers)
        {
            Should.Throw<ObjectDisposedException>(() => handler.ConnectTimeout = TimeSpan.FromSeconds(5));
        }
    }
}
