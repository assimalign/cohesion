using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.IdentityHub.Hosting.Internal;

using IPAddress = System.Net.IPAddress;
using IPEndPoint = System.Net.IPEndPoint;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public sealed class HealthProbeTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Bare health probes remain open to remote peers")]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/livez")]
    public async Task InvokeAsync_RemoteProbe_ShouldRemainOpen(string path)
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

    private static IdentityEndpointService CreateEndpoint(string dataPath) =>
        new(new Uri("http://127.0.0.1:8443/issuer"), dataPath,
            new Dictionary<string, IdentityHubClientRegistration>(), AppEnvironment.Keys.Local,
            new IdentityHubApplicationContext(AppEnvironment.Keys.Local, contentRootPath: null));

    private static ControlPlaneExchange CreateExchange(string path, HttpMethod method, string address)
    {
        var exchange = new ControlPlaneExchange(path, method) { RequestCancelled = CancellationToken.None };
        exchange.Connection.RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 12345);
        return exchange;
    }
}
