using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

public sealed class StandaloneAdminControlPlaneTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone remote health aliases cannot bypass the loopback rule")]
    [InlineData("/cohesion/v1/healthz", "GET")]
    [InlineData("/cohesion/v1/healthz", "HEAD")]
    [InlineData("/cohesion/v1/readyz", "GET")]
    [InlineData("/cohesion/v1/readyz", "HEAD")]
    [InlineData("/cohesion/v1/livez", "GET")]
    [InlineData("/cohesion/v1/livez", "HEAD")]
    public async Task ExecuteAsync_StandaloneRemoteProbeWithForwardedLoopback_ShouldForbid(string path, string method)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application, new ResourceContext());
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var exchange = new DatabaseControlPlaneExchange(
            path, new HttpMethod(method), new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));
        exchange.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        exchange.Request.Headers["Forwarded"] = "for=\"[::1]\"";

        // Act
        await pipeline.ExecuteAsync(exchange, CancellationToken.None);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone loopback probes retain Database health reports")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task ExecuteAsync_StandaloneLoopbackProbe_ShouldKeepDatabaseReport(string peer)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application, new ResourceContext());
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        foreach (string probe in new[] { "healthz", "readyz", "livez" })
        {
            await using var exchange = new DatabaseControlPlaneExchange(
                "/cohesion/v1/" + probe, HttpMethod.Get, new IPEndPoint(IPAddress.Parse(peer), 41000));

            // Act
            await pipeline.ExecuteAsync(exchange, CancellationToken.None);

            // Assert
            exchange.Response.StatusCode.ShouldBe(probe == "livez" ? HttpStatusCode.Ok : HttpStatusCode.ServiceUnavailable);
            using JsonDocument report = JsonDocument.Parse(((MemoryStream)exchange.Response.Body).ToArray());
            report.RootElement.TryGetProperty("entries", out _).ShouldBeTrue();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: standalone bare probes remain open to remote callers")]
    [InlineData("/healthz", 503)]
    [InlineData("/readyz", 503)]
    [InlineData("/livez", 200)]
    public async Task ExecuteAsync_StandaloneRemoteBareProbe_ShouldRemainOpen(string path, int expected)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application, new ResourceContext());
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var exchange = new DatabaseControlPlaneExchange(
            path, HttpMethod.Get, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));

        // Act
        await pipeline.ExecuteAsync(exchange, CancellationToken.None);

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(expected));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Admin: managed namespaced health aliases retain their existing public behavior")]
    [InlineData("/cohesion/v1/healthz", 503)]
    [InlineData("/cohesion/v1/readyz", 503)]
    [InlineData("/cohesion/v1/livez", 200)]
    public async Task ExecuteAsync_GatewayManagedRemoteProbe_ShouldPreserveBehavior(string path, int expected)
    {
        // Arrange
        await using DatabaseApplication application = DatabaseApplication.CreateBuilder().Build();
        using DatabaseAdminEndpointService service = CreateService(application, new ResourceContext(gatewayName: "gateway"));
        IWebApplicationPipeline pipeline = service.Pipeline.Build();
        await using var exchange = new DatabaseControlPlaneExchange(
            path, HttpMethod.Get, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));

        // Act
        await pipeline.ExecuteAsync(exchange, CancellationToken.None);

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(expected));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    private static DatabaseAdminEndpointService CreateService(DatabaseApplication application, ResourceContext resourceContext)
    {
        return new DatabaseAdminEndpointService(
            new Uri("http://127.0.0.1:8080"),
            ResourceControlPlane.Create(),
            resourceContext,
            application.Context,
            []);
    }
}
