using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class DatabaseHealthEndpointTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health: explicit unmanaged endpoint and managed http binding answer probes (#1790)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddHealthEndpoint_ConfiguredApplication_ShouldServeAndReleaseListener(bool managed)
    {
        // Arrange: keep both ports reserved during Build. A managed host must override the
        // explicit endpoint, which stays occupied until the end of the test.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var assigned = new TcpListener(IPAddress.Loopback, 0);
        using var explicitBind = new TcpListener(IPAddress.Loopback, 0);
        assigned.Start();
        explicitBind.Start();
        int port = ((IPEndPoint)assigned.LocalEndpoint).Port;
        var endpoint = new Uri($"http://127.0.0.1:{port}");
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = managed ? "test" : "",
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
        }, guard.Token));
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddHealthEndpoint(new Uri($"http://127.0.0.1:{((IPEndPoint)explicitBind.LocalEndpoint).Port}"))
            .ShouldBeSameAs(builder);
        if (!managed)
        {
            builder.AddHealthEndpoint(endpoint); // Last explicit configuration wins.
        }
        await using DatabaseApplication application = builder.Build();
        application.Context.HostedServices.OfType<DatabaseAdminEndpointService>().ShouldHaveSingleItem();
        assigned.Stop();
        using var client = new HttpClient { BaseAddress = endpoint };

        // Act
        await ((IHost)application).StartAsync(guard.Token);

        // Assert: Start awaited the bind, so no retry or delay is needed.
        foreach (string path in new[] { "/healthz", "/readyz", "/livez", "/cohesion/v1/healthz", "/cohesion/v1/readyz", "/cohesion/v1/livez" })
        {
            using HttpResponseMessage response = await client.GetAsync(path, guard.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await ((IHost)application).StopAsync(guard.Token);
        // A fresh connection observes listener shutdown without depending on the OS's
        // reuse policy for accepted connections that are still in TIME_WAIT.
        using var peer = new TcpClient();
        SocketException refusal = await Should.ThrowAsync<SocketException>(async () =>
            await peer.ConnectAsync(IPAddress.Loopback, port, guard.Token));
        refusal.SocketErrorCode.ShouldBe(SocketError.ConnectionRefused);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Health: unmanaged platform values alone create no listener (#1790)")]
    public async Task Build_WithoutGatewayOrExplicitEndpoint_ShouldCreateNoHealthListener()
    {
        // Arrange
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = "127.0.0.1",
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = "http",
        }, CancellationToken.None));

        // Act
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();

        // Assert
        application.Context.HostedServices.OfType<DatabaseAdminEndpointService>().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health: unsupported platform assignments keep explicit health or omit the listener (#1790)")]
    [InlineData("https", "127.0.0.1", false)]
    [InlineData("https", "127.0.0.1", true)]
    [InlineData("tcp", "127.0.0.1", false)]
    [InlineData("tcp", "127.0.0.1", true)]
    [InlineData("http", "example.test", false)]
    [InlineData("http", "example.test", true)]
    public async Task Build_UnsupportedPlatformHealthEndpoint_ShouldKeepApplicationAvailable(string scheme, string host, bool explicitHealth)
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}");
        using var environment = AppEnvironment.Enter(AppEnvironmentScope.Create(new Dictionary<string, string>
        {
            [AppEnvironment.Variables.Gateway] = "test",
            [AppEnvironment.Variables.Endpoint("http", "HOST")] = host,
            [AppEnvironment.Variables.Endpoint("http", "PORT")] = "5740",
            [AppEnvironment.Variables.Endpoint("http", "SCHEME")] = scheme,
        }, guard.Token));
        int constructions = 0;
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine("recording-engine", _ => { constructions++; return new RecordingEngine(); });
        if (explicitHealth) { builder.AddHealthEndpoint(endpoint); }

        // Act
        await using DatabaseApplication application = builder.Build();
        reservation.Stop();
        await ((IHost)application).StartAsync(guard.Token);

        // Assert
        constructions.ShouldBe(1);
        application.Context.State.ShouldBe(HostState.Started);
        application.Context.HostedServices.OfType<DatabaseAdminEndpointService>().Count().ShouldBe(explicitHealth ? 1 : 0);
        if (explicitHealth)
        {
            using var client = new HttpClient { BaseAddress = endpoint };
            using HttpResponseMessage response = await client.GetAsync("/readyz", guard.Token);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        await ((IHost)application).StopAsync(guard.Token);
        Should.Throw<InvalidOperationException>(() => builder.Services.AddSingleton(new object()));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Health: unsupported listener configuration is refused at registration (#1790)")]
    [InlineData("https://127.0.0.1:5740")]
    [InlineData("tcp://127.0.0.1:5740")]
    [InlineData("http://example.test:5740")]
    public async Task AddHealthEndpoint_UnsupportedEndpoint_ShouldRefuse(string endpoint)
    {
        // Arrange
        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        // Act / Assert
        Should.Throw<InvalidOperationException>(() => builder.AddHealthEndpoint(new Uri(endpoint)));
        Should.Throw<ArgumentNullException>(() => builder.AddHealthEndpoint(null!));
        await using DatabaseApplication application = builder.Build();
        Should.Throw<InvalidOperationException>(() => builder.AddHealthEndpoint(new Uri("http://127.0.0.1:5740")));
    }
}
