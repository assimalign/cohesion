using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;

using DnsEndPoint = System.Net.DnsEndPoint;
using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public sealed class StandaloneControlPlaneTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Standalone control plane refuses remote management and namespaced probes")]
    [InlineData("/cohesion/v1", false)]
    [InlineData("/cohesion/v1/unknown", false)]
    [InlineData("/cohesion/v1/endpoints", false)]
    [InlineData("/cohesion/v1/commands", true)]
    [InlineData("/cohesion/v1/stop", true)]
    [InlineData("/cohesion/v1/healthz", false)]
    [InlineData("/cohesion/v1/readyz", false)]
    [InlineData("/cohesion/v1/livez", false)]
    [InlineData("/cohesion/v1/configuration", false)]
    [InlineData("/cohesion/v1/configuration/status", false)]
    [InlineData("/cohesion/v1/actions/ping", true)]
    public async Task InvokeAsync_StandaloneRemotePeer_ShouldRefuseNamespacedRoutes(string path, bool post)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path);
        await using var exchange = CreateExchange("/issuer" + path, post ? HttpMethod.Post : HttpMethod.Get, "192.0.2.1");
        bool nextCalled = false;

        // Act
        await endpoint.InvokeAsync(exchange, _ => { nextCalled = true; return Task.CompletedTask; });

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        exchange.Response.Body.Length.ShouldBe(0);
        nextCalled.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Standalone control plane accepts the explicit loopback peers")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task InvokeAsync_StandaloneLoopbackPeer_ShouldServeManagement(string address)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path);
        await using var exchange = CreateExchange("/issuer/cohesion/v1/endpoints", HttpMethod.Get, address);

        // Act
        await endpoint.InvokeAsync(exchange, _ => Task.CompletedTask);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Forwarded loopback values cannot spoof a standalone transport peer")]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("127.0.0.2")]
    public async Task InvokeAsync_StandaloneForwardedLoopback_ShouldRefuseRemotePeer(string address)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path);
        await using var exchange = CreateExchange("/issuer/cohesion/v1/endpoints", HttpMethod.Get, address);
        exchange.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        exchange.Request.Headers["Forwarded"] = "for=127.0.0.1";

        // Act
        await endpoint.InvokeAsync(exchange, _ => Task.CompletedTask);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Standalone management fails closed without an IP transport endpoint")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_StandaloneUnknownPeer_ShouldRefuseManagement(bool dnsEndpoint)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path);
        await using var exchange = CreateExchange("/issuer/cohesion/v1/endpoints", HttpMethod.Get, "127.0.0.1");
        exchange.Connection.RemoteEndPoint = dnsEndpoint ? new DnsEndPoint("localhost", 12345) : null;

        // Act
        await endpoint.InvokeAsync(exchange, _ => Task.CompletedTask);

        // Assert
        exchange.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Standalone bare health probes remain open to remote peers")]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    public async Task InvokeAsync_StandaloneRemoteProbe_ShouldRemainOpen(string path)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path);
        await using var exchange = CreateExchange("/issuer" + path, HttpMethod.Get, "192.0.2.1");

        // Act
        await endpoint.InvokeAsync(exchange, _ => Task.CompletedTask);

        // Assert
        exchange.Response.StatusCode.ShouldBe(path == "/readyz" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Ok);
        exchange.Response.Body.Length.ShouldBeGreaterThan(0);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Managed remote control plane keeps gateway credential behavior")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ManagedRemotePeer_ShouldRetainCredentialChecks(bool credential)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using var identity = new TestBootstrapIdentity("appa", "gateway");
        var resource = new ResourceContext("appa", "identity", AppEnvironment.Keys.Local, "gateway",
            contentRootPath: null, endpoints: null, mounts: null, settings: null, references: null,
            bootstrapCredential: default, applicationTrustKey: identity.PublicKey, ambientValues: null);
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path, resource);
        await using var exchange = CreateExchange("/issuer/cohesion/v1/endpoints", HttpMethod.Get, "192.0.2.1");
        if (credential)
        {
            exchange.Request.Headers[HttpHeaderKey.Authorization] = "Bearer " + identity.Issue("identity");
        }

        // Act
        await endpoint.InvokeAsync(exchange, _ => Task.CompletedTask);

        // Assert
        exchange.Response.StatusCode.ShouldBe(credential ? HttpStatusCode.Ok : HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - The runtime surface, its status and its actions are served on the control plane")]
    public async Task InvokeAsync_RuntimeSurfaceRoutes_ShouldServeTheControlPlane()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        using IdentityEndpointService endpoint = CreateEndpoint(data.Path, controlPlane: RuntimeSurfaceTestObjects.CreateControlPlane());
        await using var surface = CreateExchange("/issuer/cohesion/v1/configuration", HttpMethod.Get, "127.0.0.1");
        await using var status = CreateExchange("/issuer/cohesion/v1/configuration/status", HttpMethod.Get, "127.0.0.1");
        await using var ping = CreateExchange("/issuer/cohesion/v1/actions/ping", HttpMethod.Post, "127.0.0.1");
        await using var missing = CreateExchange("/issuer/cohesion/v1/actions/missing", HttpMethod.Post, "127.0.0.1");
        await ping.Request.Body.WriteAsync("""{"text":"hi"}"""u8.ToArray());
        ping.Request.Body.Position = 0;

        // Act
        await endpoint.InvokeAsync(surface, _ => Task.CompletedTask);
        await endpoint.InvokeAsync(status, _ => Task.CompletedTask);
        await endpoint.InvokeAsync(ping, _ => Task.CompletedTask);
        await endpoint.InvokeAsync(missing, _ => Task.CompletedTask);

        // Assert
        surface.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(surface.Response.Body))
        {
            document.RootElement.GetProperty("schema").GetString().ShouldBe("cohesion/runtime/v1");
            document.RootElement.GetProperty("actions")[0].GetProperty("name").GetString().ShouldBe("ping");
        }

        status.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(status.Response.Body))
        {
            document.RootElement.GetProperty("surfaceDigest").GetString().ShouldStartWith("sha256:");
        }

        ping.Response.StatusCode.ShouldBe(HttpStatusCode.Ok);
        using (JsonDocument document = RuntimeSurfaceTestObjects.ReadBody(ping.Response.Body))
        {
            document.RootElement.GetProperty("text").GetString().ShouldBe("hi");
        }

        missing.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static IdentityEndpointService CreateEndpoint(string dataPath, ResourceContext? resource = null, IResourceControlPlane? controlPlane = null) =>
        new(new Uri("http://127.0.0.1:8443/issuer"), dataPath, Array.Empty<string>(),
            new Dictionary<string, IdentityHubClientRegistration>(), controlPlane ?? ResourceControlPlane.Create(),
            resource ?? new ResourceContext(environmentName: AppEnvironment.Keys.Local),
            new IdentityHubApplicationContext(AppEnvironment.Keys.Local, contentRootPath: null));

    private static ControlPlaneExchange CreateExchange(string path, HttpMethod method, string address)
    {
        var exchange = new ControlPlaneExchange(path, method) { RequestCancelled = CancellationToken.None };
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 12345);
        return exchange;
    }
}
