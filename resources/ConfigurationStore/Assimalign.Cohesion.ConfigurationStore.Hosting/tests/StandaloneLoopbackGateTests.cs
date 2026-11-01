using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ConfigurationStore.Hosting.Internal;
using Assimalign.Cohesion.Http;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

public sealed class StandaloneLoopbackGateTests
{
    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: standalone non-loopback callers are forbidden on the complete /cohesion/v1 namespace")]
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
        foreach (string route in new[] { "", "/", "/healthz", "/readyz", "/livez", "/endpoints", "/namespaces", "/unknown" })
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

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.Hosting] - Invoke: standalone loopback callers can read the endpoint listing")]
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

    private static ConfigurationEndpointService CreateService(string basePath = "")
    {
        var repository = new ConfigurationStoreRepository(
            Path.Combine(Path.GetTempPath(), "cohesion-configuration-loopback-gate", Guid.NewGuid().ToString("N")),
            new Dictionary<string, IReadOnlyDictionary<string, string?>>());
        return new ConfigurationEndpointService(
            new Uri("http://127.0.0.1:8080" + basePath),
            repository,
            new ConfigurationStoreApplicationContext("test", null));
    }

    private static Task UnexpectedNext(IHttpContext context)
    {
        throw new InvalidOperationException("A /cohesion/v1 route must not reach the application pipeline.");
    }
}
