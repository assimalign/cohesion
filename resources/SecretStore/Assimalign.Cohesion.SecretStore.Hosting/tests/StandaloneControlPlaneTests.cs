using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.SecretStore.Hosting.Internal;

using HttpStatusCode = Assimalign.Cohesion.Http.HttpStatusCode;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class StandaloneControlPlaneTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Runtime state protocol: catalog remains reachable while bootstrap gates secrets")]
    [InlineData("/management/v1/runtime", "127.0.0.1", 200)]
    [InlineData("/management/v1/runtime", "192.0.2.10", 403)]
    [InlineData("/cohesion/v1/secrets", "127.0.0.1", 503)]
    [InlineData("/readyz", "127.0.0.1", 503)]
    public async Task InvokeAsync_WithPendingBootstrap_ShouldExposeCatalogAndGateData(string path, string peer, int expected)
    {
        using var data = new TemporaryDirectory();
        ResourceContext resource = CreateStandaloneContext(data.Path);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.ConfigureRuntimeManagement(new ResourceRuntimeManagement { Endpoint = "api", Path = "/management/v1" });
        plane.RequireStateBootstrap();
        IServiceProvider services = application.Context.ServiceProvider;
        using var endpoint = new SecretsEndpointService(new Uri("http://127.0.0.1:8443"),
            (SecretStoreRepository)services.GetService(typeof(SecretStoreRepository))!,
            (TrustedIssuerStore)services.GetService(typeof(TrustedIssuerStore))!,
            (CertificateAuthorityManager)services.GetService(typeof(CertificateAuthorityManager))!,
            plane, resource, application.Context);
        await using var exchange = new ControlPlaneExchange(path, HttpMethod.Get, new IPEndPoint(IPAddress.Parse(peer), 12345));
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Management routes are terminal."));
        ((int)exchange.Response.StatusCode).ShouldBe(expected);
        if (expected == 200) { exchange.Response.Body.Length.ShouldBeGreaterThan(0); }
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Standalone: non-loopback peers cannot reach namespaced routes")]
    [InlineData("/cohesion/v1", "GET")]
    [InlineData("/cohesion/v1/healthz", "GET")]
    [InlineData("/cohesion/v1/readyz", "GET")]
    [InlineData("/cohesion/v1/livez", "GET")]
    [InlineData("/cohesion/v1/endpoints", "GET")]
    [InlineData("/cohesion/v1/stop", "POST")]
    [InlineData("/cohesion/v1/commands", "GET")]
    [InlineData("/cohesion/v1/commands", "POST")]
    [InlineData("/cohesion/v1/secrets", "GET")]
    [InlineData("/cohesion/v1/certificates", "GET")]
    [InlineData("/cohesion/v1/certificates/enrollment-request", "GET")]
    [InlineData("/cohesion/v1/certificates/enroll", "POST")]
    [InlineData("/cohesion/v1/certificates/enrollment", "POST")]
    [InlineData("/cohesion/v1/unknown", "GET")]
    public async Task InvokeAsync_StandaloneNonLoopbackCaller_ShouldForbidNamespacedRoute(string path, string method)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ResourceContext resource = CreateStandaloneContext(directory.Path);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
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
    public async Task InvokeAsync_StandaloneLoopbackCaller_ShouldServeCommands(string address)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ResourceContext resource = CreateStandaloneContext(directory.Path);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Get,
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
        ResourceContext resource = CreateStandaloneContext(directory.Path);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        EndPoint? remote = address is null ? null : IPAddress.TryParse(address, out IPAddress? ip)
            ? new IPEndPoint(ip, 50000) : new DnsEndPoint(address, 50000);
        await using var exchange = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Get, remote);

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
        ResourceContext resource = SecretStoreTestHost.CreateContext(
            new Uri("http://127.0.0.1:8443/store"), directory.Path, gatewayName: null);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange("/store/cohesion/v1/commands", HttpMethod.Get,
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
        ResourceContext resource = CreateStandaloneContext(directory.Path);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        await using var exchange = new ControlPlaneExchange(path, HttpMethod.Get,
            new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000));

        // Act
        await endpoint.InvokeAsync(exchange, _ => throw new InvalidOperationException("Health routes are terminal."));

        // Assert
        exchange.Response.StatusCode.ShouldBe(new HttpStatusCode(status));
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Managed: remote callers still require and accept the gateway credential")]
    public async Task InvokeAsync_GatewayManagedRemoteCaller_ShouldPreserveCredentialAuthentication()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var identity = new TestBootstrapIdentity();
        string token = identity.Issue("secrets");
        ResourceContext resource = SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(), directory.Path, token, identity.PublicKey);
        using IDisposable scope = ResourceRuntime.CreateScope(resource);
        await using SecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();
        SecretsEndpointService endpoint = application.Context.HostedServices.OfType<SecretsEndpointService>().Single();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ((IHost)application).StartAsync(cancellation.Token);
        try
        {
            await using var accepted = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Get,
                new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50000));
            accepted.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + token;
            await using var refused = new ControlPlaneExchange("/cohesion/v1/commands", HttpMethod.Get,
                new IPEndPoint(IPAddress.Parse("192.0.2.10"), 50001));

            // Act
            await endpoint.InvokeAsync(accepted, _ => throw new InvalidOperationException("Control-plane routes are terminal."));
            await endpoint.InvokeAsync(refused, _ => throw new InvalidOperationException("Control-plane routes are terminal."));

            // Assert
            accepted.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
            accepted.Response.Body.Length.ShouldBeGreaterThan(0);
            refused.Response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            refused.Response.Headers[HttpHeaderKey.WWWAuthenticate].Value.ShouldBe("Bearer");
        }
        finally
        {
            await ((IHost)application).StopAsync(cancellation.Token);
        }
    }

    private static ResourceContext CreateStandaloneContext(string dataPath) =>
        SecretStoreTestHost.CreateContext(new Uri("http://127.0.0.1:8443"), dataPath, gatewayName: null);
}
