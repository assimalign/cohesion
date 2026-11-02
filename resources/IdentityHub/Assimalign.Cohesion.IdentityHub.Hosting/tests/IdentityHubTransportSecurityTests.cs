using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.IdentityHub;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Tests;

public sealed class IdentityHubTransportSecurityTests
{
    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Transport security: unsafe certificate and plaintext fallbacks are rejected")]
    [InlineData(AppEnvironment.Keys.Production, "https://127.0.0.1:8443", "tls")]
    [InlineData(AppEnvironment.Keys.Development, "https://127.0.0.1:8443", "tls")]
    [InlineData(AppEnvironment.Keys.Development, "https://0.0.0.0:8443", "tls")]
    [InlineData(AppEnvironment.Keys.Local, "https://0.0.0.0:8443", "tls")]
    [InlineData(AppEnvironment.Keys.Production, "http://127.0.0.1:8443", "plaintext")]
    [InlineData(AppEnvironment.Keys.Development, "http://127.0.0.1:8443", "plaintext")]
    [InlineData(AppEnvironment.Keys.Development, "http://0.0.0.0:8443", "plaintext")]
    [InlineData(AppEnvironment.Keys.Local, "http://0.0.0.0:8443", "plaintext")]
    public void Build_WithoutConfiguredTransportSecurity_ShouldRejectUnsafeEndpoint(
        string environmentName,
        string endpointValue,
        string expectedMessage)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        IdentityHubApplicationBuilder builder = IdentityHubTestHost.CreateBuilder(
            data.Path,
            new Uri(endpointValue, UriKind.Absolute),
            environmentName: environmentName);

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => builder.Build());

        // Assert
        exception.Message.ShouldContain(expectedMessage, Case.Insensitive);
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub.Hosting] - Local TLS: loopback permits the self-signed fallback")]
    [InlineData(AppEnvironment.Keys.Local)]
    [InlineData("local")]
    public async Task StartAsync_WithLocalLoopbackAndNoCertificate_ShouldUseSelfSignedFallback(string environmentName)
    {
        // Arrange
        using var data = new TemporaryDirectory();
        Uri httpEndpoint = IdentityHubTestHost.GetEndpoint();
        var endpoint = new Uri($"https://127.0.0.1:{httpEndpoint.Port}", UriKind.Absolute);
        await using IdentityHubApplication application = IdentityHubTestHost.CreateBuilder(
            data.Path,
            endpoint,
            environmentName: environmentName).Build();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        await ((IHost)application).StartAsync(timeout.Token);
        try
        {
            // Assert
            application.Context.State.ShouldBe(HostState.Started);
        }
        finally
        {
            await ((IHost)application).StopAsync(timeout.Token);
        }
    }
}
