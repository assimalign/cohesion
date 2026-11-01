using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class StandaloneLoopbackGateTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: non-loopback peers cannot reach namespaced routes")]
    [InlineData("/cohesion/v1", "GET")]
    [InlineData("/cohesion/v1/healthz", "GET")]
    [InlineData("/cohesion/v1/readyz", "GET")]
    [InlineData("/cohesion/v1/livez", "GET")]
    [InlineData("/cohesion/v1/endpoints", "GET")]
    [InlineData("/cohesion/v1/secrets", "GET")]
    [InlineData("/cohesion/v1/certificates", "GET")]
    [InlineData("/cohesion/v1/certificates/enroll", "POST")]
    [InlineData("/cohesion/v1/certificates/enrollment", "POST")]
    [InlineData("/cohesion/v1/unknown", "GET")]
    public async Task InvokeAsync_StandaloneNonLoopbackCaller_ShouldForbidNamespacedRoute(string path, string method)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        await using SecretStoreApplication application = CreateStandaloneBuilder(directory.Path).Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange(path, new HttpMethod(method),
            new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000));

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Control-plane routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
        exchange.Response.Headers.ContainsKey(HttpHeaderKey.WWWAuthenticate).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: only exact loopback transport addresses are accepted")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task InvokeAsync_StandaloneLoopbackCaller_ShouldServeEndpoints(string address)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        await using SecretStoreApplication application = CreateStandaloneBuilder(directory.Path).Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get,
            new IPEndPoint(IPAddress.Parse(address), 50000));

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Control-plane routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: unknown, non-IP, and non-loopback peers fail closed")]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("127.0.0.2")]
    [InlineData("::ffff:127.0.0.2")]
    [InlineData("::ffff:192.0.2.10")]
    [InlineData("2001:db8::1")]
    public async Task InvokeAsync_StandaloneInvalidPeer_ShouldForbid(string? address)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        await using SecretStoreApplication application = CreateStandaloneBuilder(directory.Path).Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        EndPoint? remote = address is null ? null : IPAddress.TryParse(address, out IPAddress? ip)
            ? new IPEndPoint(ip, 50000) : new DnsEndPoint(address, 50000);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/endpoints", HttpMethod.Get, remote);

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Control-plane routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: forwarded loopback headers cannot bypass the prefixed route boundary")]
    public async Task InvokeAsync_StandaloneForwardedLoopbackSpoof_ShouldForbid()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder(
            new Uri("http://127.0.0.1:8443/store"), directory.Path).Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange("/store/cohesion/v1/endpoints", HttpMethod.Get,
            new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000));
        exchange.Request.Headers[new HttpHeaderKey("X-Forwarded-For")] = "127.0.0.1";
        exchange.Request.Headers[new HttpHeaderKey("Forwarded")] = "for=\"[::1]\"";

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Control-plane routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: bare probes remain available to remote callers")]
    [InlineData("/healthz", 200)]
    [InlineData("/readyz", 503)]
    [InlineData("/livez", 200)]
    public async Task InvokeAsync_StandaloneRemoteBareProbe_ShouldPreserveHealthResponse(string path, int status)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        await using SecretStoreApplication application = CreateStandaloneBuilder(directory.Path).Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange(path, HttpMethod.Get,
            new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000));

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Health routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(status));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    private static SecretStoreApplicationBuilder CreateStandaloneBuilder(string dataPath) =>
        SecretStoreTestHost.CreateBuilder(new Uri("http://127.0.0.1:8443"), dataPath);
}
