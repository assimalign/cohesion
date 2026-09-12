using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.IoTHub.Hosting.Tests;

public sealed class ResourceControlPlaneTests
{
    static ResourceControlPlaneTests() => ResourceRuntime.RegisterControlPlane(
        typeof(ResourceControlPlaneTests).Assembly, static () => ResourceControlPlane.Create());

    [Theory(DisplayName = "Cohesion Test [IoTHub.Hosting] - Control plane: managed and standalone resource routes")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ControlPlane_WithEndpoint_ShouldServeProbesAndManagement(bool managed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Uri endpoint = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort());
        using var identity = new TestBootstrapIdentity("tests", "gateway");
        string token = identity.Issue("resource");
        var resource = new ResourceContext(
            applicationName: "tests", resourceName: "resource", environmentName: "Development",
            gatewayName: managed ? "gateway" : null, contentRootPath: null, mounts: null, settings: null, references: null, ambientValues: null,
            endpoints: new Dictionary<string, Uri> { ["http"] = endpoint },
            bootstrapCredential: managed ? Encoding.UTF8.GetBytes(token) : ReadOnlyMemory<byte>.Empty,
            applicationTrustKey: managed ? identity.PublicKey : ReadOnlyMemory<byte>.Empty);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using IIoTHubApplication application = CreateBuilder(typeof(ResourceControlPlaneTests).Assembly).Build();
        Task run = application.RunAsync(timeout.Token);
        while (application.Context.State is not HostState.Started)
        {
            if (run.IsCompleted)
            {
                await run;
                throw new InvalidOperationException("The resource exited before completing startup.");
            }
            await Task.Delay(10, timeout.Token);
        }
        using var handler = new HttpClientHandler();

        using var client = new HttpClient(handler) { BaseAddress = endpoint };

        using HttpResponseMessage ready = await client.GetAsync("/readyz", timeout.Token);
        using HttpResponseMessage missing = await client.GetAsync("/cohesion/v1/endpoints", timeout.Token);
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        missing.StatusCode.ShouldBe(managed ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        if (managed) { client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token); }
        using HttpResponseMessage namespaced = await client.GetAsync("/cohesion/v1/readyz", timeout.Token);
        using HttpResponseMessage endpoints = await client.GetAsync("/cohesion/v1/endpoints", timeout.Token);
        namespaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        endpoints.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await endpoints.Content.ReadAsStringAsync(timeout.Token)).ShouldContain(endpoint.ToEndpointString(), Case.Sensitive);

        using var envelope = new StringContent("{\"id\":\"one\",\"kind\":\"unknown\",\"owner\":\"tests\",\"key\":\"one\"}", Encoding.UTF8, "application/json");
        using HttpResponseMessage refused = await client.PostAsync("/cohesion/v1/commands", envelope, timeout.Token);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        using JsonDocument body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(timeout.Token));
        body.RootElement.GetProperty("status").GetString().ShouldBe("Rejected");
        body.RootElement.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        using HttpResponseMessage stop = await client.PostAsync("/cohesion/v1/stop", null, timeout.Token);
        stop.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await run.WaitAsync(timeout.Token);
    }

    [Fact(DisplayName = "Cohesion Test [IoTHub.Hosting] - Unregistered resource: builds the plain host without a listener")]
    public async Task Build_WithoutRegistration_ShouldLeaveHostUnconfigured()
    {
        using IDisposable scope = ResourceRuntime.CreateScope(new ResourceContext(
            endpoints: new Dictionary<string, Uri> { ["http"] = Uri.CreateEndpoint("http", "127.0.0.1", ReservePort()) }));
        await using IIoTHubApplication application = CreateBuilder(typeof(IIoTHubApplication).Assembly).Build();
        application.Context.HostedServices.ShouldBeEmpty();
        ResourceRuntime.TryGetControlPlane(application, out _).ShouldBeFalse();
        await application.StartAsync(CancellationToken.None);
        await application.StopAsync(CancellationToken.None);
    }

    private static IIoTHubApplicationBuilder CreateBuilder(Assembly assembly)
    {
        MethodInfo method = typeof(IoTHubApplication).GetMethod("CreateBuilder", BindingFlags.Static | BindingFlags.NonPublic,
            binder: null, [typeof(string[]), typeof(Assembly)], modifiers: null)!;
        return (IIoTHubApplicationBuilder)method.Invoke(null, [Array.Empty<string>(), assembly])!;
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
