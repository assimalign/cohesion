using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class StandaloneControlPlaneTests
{
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: standalone non-loopback callers are forbidden on the complete control-plane namespace")]
    [InlineData("192.0.2.10", "")]
    [InlineData("2001:db8::10", "")]
    [InlineData("::ffff:192.0.2.10", "")]
    [InlineData("127.0.0.2", "")]
    [InlineData("192.0.2.10", "/store")]
    public async Task InvokeAsync_StandaloneNonLoopbackPeer_ShouldForbidControlPlane(string peer, string basePath)
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService(basePath);
        var remoteEndPoint = new IPEndPoint(IPAddress.Parse(peer), 41000);
        foreach (string route in new[] { "", "/", "/healthz", "/readyz", "/livez", "/endpoints", "/namespaces", "/commands", "/stop",
            "/configuration", "/configuration/status", "/actions/ping", "/unknown" })
        {
            foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
            {
                await using var exchange = new ConfigurationControlPlaneExchange(basePath + "/cohesion/v1" + route, method, remoteEndPoint);

                // Act
                await service.InvokeAsync(exchange, UnexpectedNext);

                // Assert
                exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, method + " " + route);
                exchange.Response.Headers.ContainsKey(HttpHeaderKey.WWWAuthenticate).ShouldBeFalse();
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: standalone loopback callers can read control-plane endpoints")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task InvokeAsync_StandaloneLoopbackPeer_ShouldAccept(string peer)
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService();
        await using var exchange = new ConfigurationControlPlaneExchange(
            "/cohesion/v1/endpoints", HttpMethod.Get, new IPEndPoint(IPAddress.Parse(peer), 41000));

        // Act
        await service.InvokeAsync(exchange, UnexpectedNext);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: a standalone remote peer cannot spoof loopback with forwarded headers")]
    public async Task InvokeAsync_StandaloneForwardedLoopbackSpoof_ShouldForbid()
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService();
        await using var exchange = new ConfigurationControlPlaneExchange(
            "/cohesion/v1/endpoints", HttpMethod.Get, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));
        exchange.Request.Headers[new HttpHeaderKey("X-Forwarded-For")] = "127.0.0.1";
        exchange.Request.Headers[new HttpHeaderKey("Forwarded")] = "for=\"[::1]\"";

        // Act
        await service.InvokeAsync(exchange, UnexpectedNext);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: absent and non-IP standalone peers are forbidden")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_StandaloneUnknownPeer_ShouldForbid(bool hasDnsEndPoint)
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService();
        await using var exchange = new ConfigurationControlPlaneExchange(
            "/cohesion/v1/endpoints", HttpMethod.Get, hasDnsEndPoint ? new DnsEndPoint("localhost", 41000) : null);

        // Act
        await service.InvokeAsync(exchange, UnexpectedNext);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: bare standalone probes remain open to non-loopback callers")]
    [InlineData("/healthz", 200)]
    [InlineData("/readyz", 503)]
    [InlineData("/livez", 200)]
    public async Task InvokeAsync_StandaloneRemoteBareProbe_ShouldRemainOpen(string path, int expected)
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService();
        await using var exchange = new ConfigurationControlPlaneExchange(
            path, HttpMethod.Get, new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000));

        // Act
        await service.InvokeAsync(exchange, UnexpectedNext);

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(expected));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: gateway-managed non-loopback callers retain credential authorization")]
    public async Task InvokeAsync_GatewayManagedRemotePeer_ShouldPreserveCredentials()
    {
        // Arrange
        string dataPath = Path.Combine(Path.GetTempPath(), "cohesion-configuration-control-plane", Guid.NewGuid().ToString("N"));
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
            string credential = identity.Issue("configuration");
            ResourceContext resourceContext = ConfigurationStoreTestHost.CreateContext(endpoint, dataPath, credential, identity.PublicKey);
            using ConfigurationEndpointService service = CreateService(resourceContext: resourceContext, endpoint: endpoint, dataPath: dataPath);
            await service.StartAsync(CancellationToken.None);
            try
            {
                var remoteEndPoint = new IPEndPoint(IPAddress.Parse("192.0.2.10"), 41000);
                await using var authorized = new ConfigurationControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get, remoteEndPoint);
                authorized.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + credential;
                await using var missing = new ConfigurationControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get, remoteEndPoint);

                // Act
                await service.InvokeAsync(authorized, UnexpectedNext);
                await service.InvokeAsync(missing, UnexpectedNext);

                // Assert
                authorized.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
                missing.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                missing.Response.Headers[HttpHeaderKey.WWWAuthenticate].Value.ShouldBe("Bearer");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: the runtime surface, its status and its actions are served on the control plane")]
    public async Task InvokeAsync_RuntimeSurfaceRoutes_ShouldServeTheControlPlane()
    {
        // Arrange
        using ConfigurationEndpointService service = CreateService(basePath: "/store", controlPlane: RuntimeSurfaceTestObjects.CreateControlPlane());
        var loopback = new IPEndPoint(IPAddress.Loopback, 41000);
        await using var surface = new ConfigurationControlPlaneExchange("/store/cohesion/v1/configuration", HttpMethod.Get, loopback);
        await using var status = new ConfigurationControlPlaneExchange("/store/cohesion/v1/configuration/status", HttpMethod.Get, loopback);
        await using var ping = new ConfigurationControlPlaneExchange("/store/cohesion/v1/actions/ping", HttpMethod.Post, loopback);
        await using var missing = new ConfigurationControlPlaneExchange("/store/cohesion/v1/actions/missing", HttpMethod.Post, loopback);
        await ping.Request.Body.WriteAsync("""{"text":"hi"}"""u8.ToArray());
        ping.Request.Body.Position = 0;

        // Act
        await service.InvokeAsync(surface, UnexpectedNext);
        await service.InvokeAsync(status, UnexpectedNext);
        await service.InvokeAsync(ping, UnexpectedNext);
        await service.InvokeAsync(missing, UnexpectedNext);

        // Assert
        surface.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(surface.Response.Body))
        {
            document.RootElement.GetProperty("schema").GetString().ShouldBe("cohesion/runtime/v1");
            document.RootElement.GetProperty("configuration")[0].GetProperty("path").GetString().ShouldBe("Store:Limits");
        }

        status.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(status.Response.Body))
        {
            document.RootElement.GetProperty("surfaceDigest").GetString().ShouldStartWith("sha256:");
            document.RootElement.GetProperty("configuration").GetProperty("sections").GetArrayLength().ShouldBe(0);
        }

        ping.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(ping.Response.Body))
        {
            document.RootElement.GetProperty("text").GetString().ShouldBe("hi");
        }

        missing.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static ConfigurationEndpointService CreateService(
        string basePath = "",
        ResourceContext? resourceContext = null,
        Uri? endpoint = null,
        string? dataPath = null,
        IResourceControlPlane? controlPlane = null)
    {
        var repository = new ConfigurationStoreRepository(
            dataPath ?? Path.Combine(Path.GetTempPath(), "cohesion-configuration-control-plane", Guid.NewGuid().ToString("N")),
            new Dictionary<string, IReadOnlyDictionary<string, string?>>());
        return new ConfigurationEndpointService(
            endpoint ?? new Uri("http://127.0.0.1:8080" + basePath),
            repository,
            controlPlane ?? ResourceControlPlane.Create(),
            resourceContext ?? new ResourceContext(environmentName: "test"),
            new ConfigurationStoreApplicationContext("test", null));
    }

    private static Task UnexpectedNext(IHttpContext context)
    {
        throw new InvalidOperationException("A control-plane route must not reach the application pipeline.");
    }
}
