using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Core;
using Assimalign.Cohesion.Hosting;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class ResourceControlPlaneHostingTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - CreateBuilder(args): honors the registered control plane and ambient admin endpoint")]
    public async Task CreateBuilderWithArgs_WhenRegistered_ShouldComposeControlPlane()
    {
        var endpoint = new EndpointAddress("http", "127.0.0.1", ReservePort());
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "ControlPlaneTest",
            endpoints: new Dictionary<string, EndpointAddress> { ["admin"] = endpoint }));

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder([]);
        builder.AddHealthCheck("builder", _ => ValueTask.FromResult(HealthContribution.Healthy()));
        builder.Options.Services.Add(new HealthyHostService("services"));

        await using DatabaseApplication application = builder.Build();
        IResourceControlPlane controlPlane = builder.ControlPlane.ShouldNotBeNull();
        ResourceHealthReport report = await controlPlane.CheckReadinessAsync(CancellationToken.None);

        controlPlane.ObservedEndpoints["admin"].ShouldBe(endpoint);
        application.Context.Environment.Name.ShouldBe("ControlPlaneTest");
        report.Contributions.Keys.ShouldContain("builder");
        report.Contributions.Keys.ShouldContain("services");

        await ((IHost)application).StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { BaseAddress = endpoint.Url };
            foreach (string path in new[]
            {
                "/healthz",
                "/readyz",
                "/livez",
            })
            {
                using HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
            }
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - CreateBuilder(): remains a plain application")]
    public void CreateBuilderWithoutArgs_ShouldRemainPlain()
    {
        DatabaseApplication.CreateBuilder().ControlPlane.ShouldBeNull();
    }

    private sealed class HealthyHostService(string name) : IHostService, IHealthContributor
    {
        public ServiceId Id { get; } = ServiceId.New();

        public string Name { get; } = name;

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
