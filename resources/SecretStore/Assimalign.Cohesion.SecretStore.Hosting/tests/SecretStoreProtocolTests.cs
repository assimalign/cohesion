using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class SecretStoreProtocolTests
{
    // The plain host takes no credential; the client presents one on every request regardless.
    private const string UnusedToken = "plain-host";

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Plaintext: deployable environments reject loopback HTTP")]
    [InlineData(AppEnvironment.Keys.Development)]
    [InlineData(AppEnvironment.Keys.Production)]
    public void Build_WithDeployableLoopbackHttp_ShouldRejectPlaintext(string environmentName)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment(environmentName);
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder(directory.Path);

        // Act
        InvalidOperationException error = Should.Throw<InvalidOperationException>(() => builder.Build());

        // Assert
        error.Message.ShouldContain("plaintext HTTP only on loopback in Local", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Local plaintext: loopback HTTP is permitted")]
    [InlineData(AppEnvironment.Keys.Local)]
    [InlineData("local")]
    public async Task Build_WithLocalLoopbackHttp_ShouldPermitPlaintext(string environmentName)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment(environmentName);
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder(directory.Path);

        // Act
        await using SecretStoreApplication application = builder.Build();

        // Assert
        application.Context.HostedServices.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Client: secret and certificate reads round trip over loopback HTTP")]
    public async Task Client_WithLoopbackHost_ShouldReadSecretAndCertificate()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using IDisposable scope = SecretStoreTestHost.EnterEnvironment();
        Uri endpoint = SecretStoreTestHost.GetEndpoint();
        SecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder(endpoint, directory.Path);
        builder.AddSecret("app/connection", Encoding.UTF8.GetBytes("Server=loopback"));
        await using SecretStoreApplication application = builder.Build();
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ((IHost)application).StartAsync(cancellationTokenSource.Token);

        try
        {
            ISecretStoreClient client = SecretStoreClient.Create(
                endpoint,
                new ClientCredential(UnusedToken));

            // Act
            ReadOnlyMemory<byte> secret = await client.GetSecretAsync(
                "app/connection",
                cancellationTokenSource.Token);
            string firstCertificate = await client.GetCertificateAsync(
                "certs/appa-api",
                cancellationTokenSource.Token);
            string secondCertificate = await client.GetCertificateAsync(
                "certs/appa-api",
                cancellationTokenSource.Token);

            // Assert
            Encoding.UTF8.GetString(secret.Span).ShouldBe("Server=loopback");
            firstCertificate.ShouldBe(secondCertificate);
            firstCertificate.ShouldContain("BEGIN CERTIFICATE", Case.Sensitive);
            firstCertificate.ShouldContain("BEGIN PRIVATE KEY", Case.Sensitive);
        }
        finally
        {
            await ((IHost)application).StopAsync(cancellationTokenSource.Token);
        }
    }
}
