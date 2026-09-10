using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public sealed class IdentityHubTransportSecurityTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Transport security: unsafe certificate and plaintext fallbacks are rejected")]
    [InlineData("Production", "https://127.0.0.1:8443", "tls")]
    [InlineData("Development", "https://0.0.0.0:8443", "tls")]
    [InlineData("Production", "http://127.0.0.1:8443", "plaintext")]
    [InlineData("Development", "http://0.0.0.0:8443", "plaintext")]
    public void Build_WithoutConfiguredTransportSecurity_ShouldRejectUnsafeEndpoint(
        string environmentName,
        string endpointValue,
        string expectedMessage)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IIdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(
            data.Path,
            new Uri(endpointValue, UriKind.Absolute),
            environmentName: environmentName);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        // Assert
        exception.Message.ShouldContain(expectedMessage, Case.Insensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub.Hosting] - TLS mount: production serves the mounted certificate and omits local device approval")]
    public async Task Start_WithMountedTlsCertificate_ShouldServeCertificateAndDisableDevelopmentApproval()
    {
        // Arrange
        using var data = new TemporaryDirectory();
        var certificate = new TestTlsCertificate();
        Uri httpEndpoint = IdentityHubTestHost.GetEndpoint();
        var endpoint = new Uri($"https://127.0.0.1:{httpEndpoint.Port}", UriKind.Absolute);
        IIdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(
            data.Path,
            endpoint,
            environmentName: "Production",
            tlsCertificate: certificate.Mount);
        await using IIdentityHubApplication application = builder.Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        bool receivedMountedCertificate = false;
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
            {
                receivedMountedCertificate = string.Equals(
                    presented?.Thumbprint,
                    certificate.Thumbprint,
                    StringComparison.OrdinalIgnoreCase);
                return receivedMountedCertificate;
            },
        };
        using var client = new HttpClient(handler);
        await application.StartAsync(timeout.Token);

        try
        {
            // Act
            using HttpResponseMessage discoveryResponse = await client.GetAsync(
                new Uri(endpoint, "/.well-known/openid-configuration"),
                timeout.Token);
            using JsonDocument discovery = JsonDocument.Parse(
                await discoveryResponse.Content.ReadAsStringAsync(timeout.Token));
            using HttpResponseMessage approval = await client.GetAsync(
                new Uri(endpoint, "/oauth2/device"),
                timeout.Token);

            // Assert
            discoveryResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
            receivedMountedCertificate.ShouldBeTrue();
            discovery.RootElement.TryGetProperty("device_authorization_endpoint", out _)
                .ShouldBeFalse();
            discovery.RootElement.GetProperty("grant_types_supported")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ShouldNotContain("urn:ietf:params:oauth:grant-type:device_code");
            discovery.RootElement.GetProperty("id_token_signing_alg_values_supported")[0]
                .GetString().ShouldBe("ES256");
            approval.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await application.StopAsync(timeout.Token);
        }
    }
}
