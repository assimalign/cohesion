using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public sealed class ResourceControlPlaneHostingTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Control plane: terminal routes run ahead of user middleware")]
    public async Task ControlPlaneRoutes_WhenRegistered_ShouldRunAheadOfUserMiddleware()
    {
        int port = ReservePort();
        var endpoint = new EndpointAddress("http", "127.0.0.1", port);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck("self", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        await using WebApplication application = builder.Build();
        bool userMiddlewareRan = false;
        application.Use((context, _) =>
        {
            userMiddlewareRan = true;
            context.Response.StatusCode = Assimalign.Cohesion.Http.HttpStatusCode.InternalServerError;
            return Task.CompletedTask;
        });

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            foreach (string path in new[]
            {
                "/healthz",
                "/readyz",
                "/livez",
                "/cohesion/v1/endpoints",
            })
            {
                using HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            string endpoints = await client.GetStringAsync(
                "/cohesion/v1/endpoints",
                CancellationToken.None);
            endpoints.ShouldContain(endpoint.ToString());
            userMiddlewareRan.ShouldBeFalse();
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(args): honors the registered control plane and ambient endpoint")]
    public async Task CreateBuilderWithArgs_WhenRegistered_ShouldComposeControlPlane()
    {
        var endpoint = new EndpointAddress("http", "127.0.0.1", 48123);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "ControlPlaneTest",
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = endpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder([]);
        builder.AddHealthCheck("builder", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        builder.Services.AddSingleton<IHealthContributor>(new HealthyContributor("services"));

        await using WebApplication application = builder.Build();
        IResourceControlPlane controlPlane = builder.ControlPlane.ShouldNotBeNull();
        ResourceHealthReport report = await controlPlane.CheckHealthAsync(CancellationToken.None);

        controlPlane.ObservedEndpoints["http"].ShouldBe(endpoint);
        application.Context.Environment.Name.ShouldBe("ControlPlaneTest");
        report.Contributions.Keys.ShouldContain("builder");
        report.Contributions.Keys.ShouldContain("services");
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(): remains a plain application")]
    public void CreateBuilderWithoutArgs_ShouldRemainPlain()
    {
        WebApplication.CreateBuilder().ControlPlane.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - CreateBuilder(args): an unregistered caller remains plain with ambient context")]
    public async Task CreateBuilderWithArgs_WhenCallerIsUnregistered_ShouldRemainPlain()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        using var ambientReservation = new TcpListener(IPAddress.Loopback, 0);
        ambientReservation.Start();
        int ambientPort = ((IPEndPoint)ambientReservation.LocalEndpoint).Port;
        int applicationPort = ReservePort();

        var ambientEndpoint = new EndpointAddress("http", "127.0.0.1", ambientPort);
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "AmbientOnly",
            endpoints: new Dictionary<string, EndpointAddress> { ["http"] = ambientEndpoint }));

        WebApplicationBuilder builder = WebApplication.CreateBuilder(
            [],
            typeof(WebApplication).Assembly);
        builder.Server.UseServer(options => options.UseHttp1(
            tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, applicationPort)));

        builder.ControlPlane.ShouldBeNull();
        builder.Environment.Name.ShouldNotBe("AmbientOnly");

        await using WebApplication application = builder.Build();
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{applicationPort}"),
            };
            foreach (string path in new[] { "/healthz", "/cohesion/v1/endpoints" })
            {
                using HttpResponseMessage response = await client.GetAsync(path, cancellation.Token);
                response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    private sealed class HealthyContributor(string name) : IHealthContributor
    {
        public string Name { get; } = name;

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(HealthContribution.Healthy());
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

internal static class ResourceControlPlaneTestRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterControlPlane(
            typeof(ResourceControlPlaneTestRegistration).Assembly,
            static () => ResourceControlPlane.Create());
    }
}
