using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

/// <summary>
/// Covers the entry-point listener defaults: a plain <c>CreateBuilder(args)</c> application that
/// configures no listener serves <c>Http:Endpoints</c>, or the loopback development endpoint when
/// none is configured; explicit compositions are left as composed.
/// </summary>
public sealed class WebApplicationDefaultEndpointTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(30);

    // The Web.Hosting assembly registers no generated control plane, so a builder created for it
    // is a plain application. (This test assembly registers one through a module initializer.)
    private static WebApplicationBuilder CreatePlainEntryPointBuilder(params string[] args)
    {
        return WebApplication.CreateBuilder(args, typeof(WebApplication).Assembly);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<HttpStatusCode> GetStatusAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/", cancellationToken);
        return response.StatusCode;
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: a plain entry point with no listener serves the development endpoint")]
    public async Task Build_PlainEntryPointWithoutListener_ShouldServeDevelopmentEndPoint()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int port = GetFreeTcpPort();
        WebApplicationBuilder builder = CreatePlainEntryPointBuilder();
        builder.DevelopmentEndPoint = new IPEndPoint(IPAddress.Loopback, port);
        await using WebApplication application = builder.Build();

        // Act
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            // Assert — any HTTP answer proves the listener; nothing handles "/", so the terminal 404s.
            (await GetStatusAsync(port, cancellation.Token)).ShouldBe(HttpStatusCode.NotFound);
            builder.Server.HasListenerConfiguration.ShouldBeTrue();
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: a plain entry point serves Http:Endpoints without UseConfiguration")]
    public async Task Build_PlainEntryPointWithConfiguredEndpoint_ShouldServeConfiguredEndpoint()
    {
        // Arrange — the development endpoint points at a second free port that must stay unbound.
        using CancellationTokenSource cancellation = new(_testTimeout);
        int configuredPort = GetFreeTcpPort();
        int developmentPort = GetFreeTcpPort();
        WebApplicationBuilder builder = CreatePlainEntryPointBuilder(
            "--Http:Endpoints:Main:Host=127.0.0.1",
            $"--Http:Endpoints:Main:Port={configuredPort}");
        builder.DevelopmentEndPoint = new IPEndPoint(IPAddress.Loopback, developmentPort);
        await using WebApplication application = builder.Build();

        // Act
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            // Assert
            (await GetStatusAsync(configuredPort, cancellation.Token)).ShouldBe(HttpStatusCode.NotFound);
            await Should.ThrowAsync<HttpRequestException>(() => GetStatusAsync(developmentPort, cancellation.Token));
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: an explicit listener suppresses the defaults")]
    public async Task Build_PlainEntryPointWithExplicitListener_ShouldNotAddDefaults()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(_testTimeout);
        int explicitPort = GetFreeTcpPort();
        int developmentPort = GetFreeTcpPort();
        WebApplicationBuilder builder = CreatePlainEntryPointBuilder();
        builder.DevelopmentEndPoint = new IPEndPoint(IPAddress.Loopback, developmentPort);
        builder.Server.UseServer(options => options.UseHttp1(tcp => tcp.EndPoint = new IPEndPoint(IPAddress.Loopback, explicitPort)));
        await using WebApplication application = builder.Build();

        // Act
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            // Assert
            (await GetStatusAsync(explicitPort, cancellation.Token)).ShouldBe(HttpStatusCode.NotFound);
            await Should.ThrowAsync<HttpRequestException>(() => GetStatusAsync(developmentPort, cancellation.Token));
        }
        finally
        {
            await ((IHost)application).StopAsync(CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: a custom server suppresses the defaults")]
    public async Task Build_PlainEntryPointWithCustomServer_ShouldNotAddDefaults()
    {
        // Arrange
        WebApplicationBuilder builder = CreatePlainEntryPointBuilder();
        ((IWebApplicationBuilder)builder).AddServer(new StubApplicationServer());

        // Act
        await using WebApplication application = builder.Build();

        // Assert
        builder.Server.HasListenerConfiguration.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Default endpoints: an explicit composition is left as composed")]
    public async Task Build_ExplicitComposition_ShouldNotAddDefaults()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions());

        // Act
        await using WebApplication application = builder.Build();

        // Assert
        builder.Server.HasListenerConfiguration.ShouldBeFalse();
    }

    private sealed class StubApplicationServer : IWebApplicationServer
    {
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
