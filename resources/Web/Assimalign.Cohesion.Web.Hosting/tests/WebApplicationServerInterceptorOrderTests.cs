using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;
using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class WebApplicationServerInterceptorOrderTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: Own interceptors precede HTTP defaults and configured interceptors")]
    public async Task Build_DefaultServer_ShouldPreserveTheChosenInterceptorOrder()
    {
        // Arrange — the transport factory runs while the public server snapshots its full list.
        await using InMemoryConnectionListener transport = new();
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IHttpExchangeInterceptor[]? observed = null;
        RequestHookInterceptor first = new();
        RequestHookInterceptor second = new();
        builder.Server.UseServer(options =>
        {
            options.Interceptors.Add(first);
            options.UseHttp1(() =>
            {
                observed = options.Interceptors.ToArray();
                return transport;
            });
        });
        builder.Server.UseServer(options => options.Interceptors.Add(second));

        // Act
        await using WebApplication application = builder.Build();
        application.Context.Servers.ShouldHaveSingleItem().ShouldBeOfType<WebApplicationServer>();

        // Assert — the first two publish exchange-local state; HTTP defaults retain their order.
        observed.ShouldNotBeNull();
        observed!.Select(interceptor => interceptor.GetType().Name).ShouldBe(new[]
        {
            "WebClientFaultInterceptor",
            "WebResponseStartingInterceptor",
            "HttpRequestLimitsInterceptor",
            "HttpProtocolUpgradeInterceptor",
            "HttpExtendedConnectInterceptor",
            nameof(RequestHookInterceptor),
            nameof(RequestHookInterceptor),
        });
        observed[5].ShouldBeSameAs(first);
        observed[6].ShouldBeSameAs(second);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server: A Services interceptor runs after defaults and before a listener callback interceptor")]
    public async Task UseServer_WithRegisteredInterceptor_ShouldRunBetweenDefaultsAndConfiguredInterceptor()
    {
        // Arrange
        List<string> calls = new();
        int factoryCalls = 0;
        RequestHookInterceptor registered = new(context =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>().ShouldNotBeNull();
            calls.Add("services");
        });
        RequestHookInterceptor configured = new(_ => calls.Add("callback"));
        await using WebApplicationTestFactory factory = new();
        IHttpExchangeInterceptor[]? observed = null;
        factory.Builder.Services.AddSingleton<IHttpExchangeInterceptor>(_ =>
        {
            factoryCalls++;
            return registered;
        });
        factory.Builder.Server.UseServer(options =>
        {
            observed = options.Interceptors.ToArray();
            options.Interceptors.Add(configured);
        });
        factory.Application.Use((context, _) =>
        {
            calls.Add("handler");
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.NoContent;
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        using HttpResponseMessage first = await client.GetAsync("/first", timeout.Token);
        using HttpResponseMessage second = await client.GetAsync("/second", timeout.Token);

        // Assert
        first.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        observed.ShouldNotBeNull();
        observed!.Select(interceptor => interceptor.GetType()).ShouldBe(new[]
        {
            typeof(HttpRequestLimitsInterceptor),
            typeof(HttpProtocolUpgradeInterceptor),
            typeof(HttpExtendedConnectInterceptor),
            typeof(RequestHookInterceptor),
        });
        observed[3].ShouldBeSameAs(registered);
        factoryCalls.ShouldBe(1);
        calls.ShouldBe(new[] { "services", "callback", "handler", "services", "callback", "handler" });
    }

    [Theory(DisplayName = "Cohesion Test [Web.Hosting] - Server: Defaults can be unregistered and replaced through Services")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseServer_WithReplacedDefault_ShouldRunTheReplacement(bool removeAll)
    {
        // Arrange
        int calls = 0;
        RequestHookInterceptor replacement = new(_ => calls++);
        await using WebApplicationTestFactory factory = new();
        if (removeAll)
        {
            factory.Builder.Services.RemoveAll<IHttpExchangeInterceptor>();
        }
        else
        {
            ServiceDescriptor descriptor = factory.Builder.Services.Container.Single(candidate =>
                candidate.ServiceType == typeof(IHttpExchangeInterceptor) &&
                candidate.ImplementationInstance is HttpProtocolUpgradeInterceptor);
            factory.Builder.Services.Container.Unregister(descriptor).ShouldBeTrue();
        }
        factory.Builder.Services.AddSingleton<IHttpExchangeInterceptor>(replacement);
        IHttpExchangeInterceptor[]? observed = null;
        factory.Builder.Server.UseServer(options => observed = options.Interceptors.ToArray());
        factory.Application.Use((context, _) =>
        {
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.NoContent;
            return Task.CompletedTask;
        });
        using HttpClient client = factory.CreateClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        // Act
        using HttpResponseMessage response = await client.GetAsync("/replacement", timeout.Token);

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NoContent);
        observed.ShouldNotBeNull();
        observed!.Select(interceptor => interceptor.GetType()).ShouldBe(removeAll
            ? new[] { typeof(RequestHookInterceptor) }
            : new[] { typeof(HttpRequestLimitsInterceptor), typeof(HttpExtendedConnectInterceptor), typeof(RequestHookInterceptor) });
        observed.Last().ShouldBeSameAs(replacement);
        factory.Application.Context.ServiceProvider.GetRequiredService<IEnumerable<IHttpExchangeInterceptor>>()
            .Last().ShouldBeSameAs(replacement);
        calls.ShouldBe(1);
    }
}
