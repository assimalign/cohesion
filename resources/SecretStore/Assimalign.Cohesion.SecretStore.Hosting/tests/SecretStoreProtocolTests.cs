using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Client;

using SecretStoreResourceCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;

namespace Assimalign.Cohesion.SecretStore.Hosting.Tests;

public sealed class SecretStoreProtocolTests
{
    [Theory(DisplayName = "Cohesion Test [SecretStore.Hosting] - Plaintext: deployable environments reject loopback HTTP")]
    [InlineData(AppEnvironment.Keys.Development, true)]
    [InlineData(AppEnvironment.Keys.Development, false)]
    [InlineData(AppEnvironment.Keys.Production, true)]
    [InlineData(AppEnvironment.Keys.Production, false)]
    public void Build_WithDeployableLoopbackHttp_ShouldRejectPlaintext(string environmentName, bool managed)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        ResourceContext context = SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: managed ? "local" : null,
            environmentName: environmentName);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();

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
        ResourceContext context = SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            gatewayName: null,
            environmentName: environmentName);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();

        // Act
        await using ISecretStoreApplication application = builder.Build();

        // Assert
        application.Context.HostedServices.ShouldNotBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Bootstrap context: rejects a credential that does not match the resource identity")]
    public async Task StartAsync_WithMismatchedAmbientCredential_ShouldRejectBootstrapContext()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var identity = new TestBootstrapIdentity();
        ResourceContext context = SecretStoreTestHost.CreateContext(
            SecretStoreTestHost.GetEndpoint(),
            directory.Path,
            identity.Issue("another-resource"),
            identity.PublicKey);
        using IDisposable scope = ResourceRuntime.CreateScope(context);
        await using ISecretStoreApplication application = SecretStoreTestHost.CreateBuilder().Build();

        // Act
        InvalidDataException error = await Should.ThrowAsync<InvalidDataException>(
            () => application.StartAsync());

        // Assert
        error.Message.ShouldContain("does not match", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Bootstrap authentication: missing token is 401, wrong audience is 403, and valid token is 200")]
    public async Task Secrets_WithMissingWrongAudienceAndValidCredentials_ShouldEnforceAuthenticationStatus()
    {
        // Arrange
        string dataPath = SecretStoreTestHost.CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            Uri endpoint = SecretStoreTestHost.GetEndpoint();
            string token = identity.Issue("secrets");
            ResourceContext context = SecretStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                token,
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
            builder.AddSecret("app/api-key", Encoding.UTF8.GetBytes("correct-horse"));
            await using ISecretStoreApplication application = builder.Build();
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await application.StartAsync(cancellationTokenSource.Token);

            try
            {
                using var client = new HttpClient();
                Uri route = new(endpoint, "/cohesion/v1/secrets?path=app%2Fapi-key");

                // Act
                using HttpResponseMessage missing = await client.GetAsync(
                    route,
                    cancellationTokenSource.Token);
                using var wrongAudienceRequest = new HttpRequestMessage(HttpMethod.Get, route);
                wrongAudienceRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    identity.Issue("another-resource"));
                using HttpResponseMessage wrongAudience = await client.SendAsync(
                    wrongAudienceRequest,
                    cancellationTokenSource.Token);
                using var validRequest = new HttpRequestMessage(HttpMethod.Get, route);
                validRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage valid = await client.SendAsync(
                    validRequest,
                    cancellationTokenSource.Token);
                byte[] value = await valid.Content.ReadAsByteArrayAsync(cancellationTokenSource.Token);

                // Assert
                missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                missing.Headers.WwwAuthenticate.ToString().ShouldBe("Bearer");
                wrongAudience.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                valid.StatusCode.ShouldBe(HttpStatusCode.OK);
                Encoding.UTF8.GetString(value).ShouldBe("correct-horse");
            }
            finally
            {
                await application.StopAsync(cancellationTokenSource.Token);
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.Hosting] - Client: secret, certificate, and trust command round trip over loopback HTTP")]
    public async Task Client_WithLoopbackHost_ShouldRoundTripSecretCertificateAndTrustUpsert()
    {
        // Arrange
        string dataPath = SecretStoreTestHost.CreateTemporaryDirectory();
        try
        {
            using var identity = new TestBootstrapIdentity();
            using var peerIdentity = new TestBootstrapIdentity("peer", "gateway");
            Uri endpoint = SecretStoreTestHost.GetEndpoint();
            string token = identity.Issue("secrets");
            ResourceContext context = SecretStoreTestHost.CreateContext(
                endpoint,
                dataPath,
                token,
                identity.PublicKey);
            using IDisposable scope = ResourceRuntime.CreateScope(context);
            ISecretStoreApplicationBuilder builder = SecretStoreTestHost.CreateBuilder();
            builder.AddSecret("app/connection", Encoding.UTF8.GetBytes("Server=loopback"));
            await using ISecretStoreApplication application = builder.Build();
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await application.StartAsync(cancellationTokenSource.Token);

            try
            {
                ISecretStoreClient client = SecretStoreClient.Create(
                    endpoint,
                    new ClientCredential(token));

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
                await client.SendCommandAsync(
                    new SecretStoreResourceCommand(
                        "trust-peer",
                        SecretsEndpointService.TrustAddCommand,
                        $"{identity.Issuer}@{identity.Subject}",
                        peerIdentity.Issuer,
                        peerIdentity.PublicKey),
                    cancellationTokenSource.Token);
                ReadOnlyMemory<byte> trustDocument = await client.GetSecretAsync(
                    "trusted-issuers.json",
                    cancellationTokenSource.Token);

                // Assert
                Encoding.UTF8.GetString(secret.Span).ShouldBe("Server=loopback");
                firstCertificate.ShouldBe(secondCertificate);
                firstCertificate.ShouldContain("BEGIN CERTIFICATE", Case.Sensitive);
                firstCertificate.ShouldContain("BEGIN PRIVATE KEY", Case.Sensitive);
                using JsonDocument document = JsonDocument.Parse(trustDocument);
                document.RootElement.GetProperty("issuers").EnumerateArray()
                    .ShouldContain(issuer => string.Equals(
                        issuer.GetProperty("issuer").GetString(),
                        peerIdentity.Issuer,
                        StringComparison.Ordinal));
            }
            finally
            {
                await application.StopAsync(cancellationTokenSource.Token);
            }
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
