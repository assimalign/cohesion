using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.SecretStore.Client;

// Both the ApplicationModel and the SecretStore client declare these names; this file means the client's.
using SecretStoreCommand = Assimalign.Cohesion.SecretStore.Client.ResourceCommand;
using SecretStoreCommandObservation = Assimalign.Cohesion.SecretStore.Client.ResourceCommandObservation;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Drives every provider through its public constructor against a real SecretStore host, so the
/// real transport, the store's bearer verification, and the store's routes and document formats
/// are all in the loop.
/// </summary>
public sealed class SecretStoreHostIntegrationTests : IClassFixture<SecretStoreHostFixture>
{
    private readonly SecretStoreHostFixture _host;

    public SecretStoreHostIntegrationTests(SecretStoreHostFixture host)
    {
        _host = host;
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: SourceProvider reads a seeded secret")]
    public async Task ReadSecretAsync_AgainstRealHost_ShouldReturnSeededSecret()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new SecretStoreSourceProvider();

        // Act
        ReadOnlyMemory<byte> secret = await provider.ReadSecretAsync(
            SecretStoreTestConnections.SecretRequest(SecretStoreHostFixture.SeededPath, _host.CreateConnection()),
            timeout.Token);

        // Assert
        secret.ToArray().ShouldBe(SecretStoreHostFixture.SeededValue);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: SourceProvider surfaces a missing secret as 404")]
    public async Task ReadSecretAsync_ForMissingPath_ShouldThrowNotFound()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new SecretStoreSourceProvider();

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => provider.ReadSecretAsync(
                SecretStoreTestConnections.SecretRequest("app/missing", _host.CreateConnection()),
                timeout.Token)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: The store forbids a credential minted for another audience")]
    public async Task ReadSecretAsync_WithCredentialForAnotherAudience_ShouldThrowForbidden()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new SecretStoreSourceProvider();
        ResourceProviderConnection connection = _host.CreateConnection() with
        {
            BearerCredential = _host.Identity.Issue("another-resource"),
        };

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => provider.ReadSecretAsync(
                SecretStoreTestConnections.SecretRequest(SecretStoreHostFixture.SeededPath, connection),
                timeout.Token)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: The store rejects a credential signed by an untrusted key")]
    public async Task ReadSecretAsync_WithUntrustedCredential_ShouldThrowUnauthorized()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var impostor = new TestTrustIdentity(SecretStoreHostFixture.Application, "local");
        var provider = new SecretStoreSourceProvider();
        ResourceProviderConnection connection = _host.CreateConnection() with
        {
            BearerCredential = impostor.Issue(SecretStoreHostFixture.Store),
        };

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => provider.ReadSecretAsync(
                SecretStoreTestConnections.SecretRequest(SecretStoreHostFixture.SeededPath, connection),
                timeout.Token)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: SourceProvider returns a certificate bundle that chains to the ca/root anchors")]
    public async Task ReadCertificateAsync_AgainstRealHost_ShouldReturnBundleChainingToAnchors()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var provider = new SecretStoreSourceProvider();

        // Act
        ResourceCertificate certificate = await provider.ReadCertificateAsync(
            SecretStoreTestConnections.SecretRequest("certs/appa-mounted", _host.CreateConnection()),
            timeout.Token);

        // Assert
        certificate.CertificatePem.ShouldContain("BEGIN CERTIFICATE", Case.Sensitive);
        certificate.CertificatePem.ShouldContain("BEGIN PRIVATE KEY", Case.Sensitive);
        ShouldChainToAnchors(certificate);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: CertificateAuthority issues a durable certs/<leaf> with the store's names")]
    public async Task IssueAsync_AgainstRealHost_ShouldIssueStableLeaf()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var authority = new SecretStoreCertificateAuthority();
        ResourceCertificateRequest request = SecretStoreTestConnections.CertificateRequest("api-https");

        // Act
        ResourceCertificate first = await authority.IssueAsync(request, _host.CreateConnection(), timeout.Token);
        ResourceCertificate second = await authority.IssueAsync(request, _host.CreateConnection(), timeout.Token);

        // Assert
        ShouldChainToAnchors(first);
        using X509Certificate2 firstLeaf = X509Certificate2.CreateFromPem(first.CertificatePem);
        using X509Certificate2 secondLeaf = X509Certificate2.CreateFromPem(second.CertificatePem);
        secondLeaf.Thumbprint.ShouldBe(firstLeaf.Thumbprint);
        second.TrustAnchorsPem.ShouldBe(first.TrustAnchorsPem);
        var names = new X509SubjectAlternativeNameExtension(firstLeaf.Extensions["2.5.29.17"]!.RawData);
        names.EnumerateDnsNames().ShouldContain("localhost");
        names.EnumerateDnsNames().ShouldNotContain("api.appa.internal");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: TrustedIssuerStore grants round-trip through cohesion.trust.add and trusted-issuers.json")]
    public async Task AddAsync_AgainstRealHost_ShouldBeReadBack()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var peer = new TestTrustIdentity("peer-roundtrip");
        using var restricted = new TestTrustIdentity("peer-restricted");
        var store = new SecretStoreTrustedIssuerStore();

        // Act
        IReadOnlyList<TrustedIssuer>? before = await store.ReadAsync(_host.CreateConnection(), timeout.Token);
        await store.AddAsync(_host.CreateConnection(), _host.Owner, peer.ToTrustedIssuer(), timeout.Token);
        await store.AddAsync(_host.CreateConnection(), _host.Owner, peer.ToTrustedIssuer(), timeout.Token);
        await store.AddAsync(_host.CreateConnection(), _host.Owner, restricted.ToTrustedIssuer("web.add-route"), timeout.Token);
        IReadOnlyList<TrustedIssuer>? after = await store.ReadAsync(_host.CreateConnection(), timeout.Token);

        // Assert
        before.ShouldNotBeNull();
        before.ShouldNotContain(issuer => issuer.Issuer == "peer-roundtrip");
        after.ShouldNotBeNull();
        TrustedIssuer stored = after.Single(issuer => issuer.Issuer == "peer-roundtrip");
        stored.PublicKey.GetProperty("kid").GetString()
            .ShouldBe(peer.ToTrustedIssuer().PublicKey.GetProperty("kid").GetString());
        stored.AllowedCommandKinds.ShouldBeEmpty();
        after.Single(issuer => issuer.Issuer == "peer-restricted").AllowedCommandKinds.ShouldBe(["web.add-route"]);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: The store refuses a grant whose owner is not the caller")]
    public async Task AddAsync_WithOwnerOtherThanCaller_ShouldThrowForbidden()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var peer = new TestTrustIdentity("peer-forbidden");
        var store = new SecretStoreTrustedIssuerStore();

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => store.AddAsync(
                _host.CreateConnection(),
                "appa@someone-else",
                peer.ToTrustedIssuer(),
                timeout.Token)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - Real host: The resolved add-secret payload is applied by the store and readable through the source provider")]
    public async Task ResolveAsync_DeliveredToRealHost_ShouldStoreResolvedValue()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] value = Encoding.UTF8.GetBytes("resolved-at-delivery");
        var sources = new StubSourceResolver().Resolve("parameter:api-token", value);
        var declared = new ResourceCommandInput(
            "add-secret-integration",
            "secretstore.add-secret",
            SecretStoreHostFixture.Application,
            "orders/api-token",
            Encoding.UTF8.GetBytes("""{"path":"orders/api-token","source":"parameter:api-token"}"""));
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, timeout.Token);
        ResourceProviderConnection connection = _host.CreateConnection();
        using var transport = new HttpMessageInvoker(new SocketsHttpHandler(), disposeHandler: true);
        ISecretStoreClient client = SecretStoreClient.CreateForControlPlane(
            connection.ControlPlaneAddress,
            new ClientCredential(connection.BearerCredential),
            transport);

        // Act
        SecretStoreCommandObservation observation = await client.ObserveCommandAsync(
            new SecretStoreCommand(declared.Id, declared.Kind, declared.Owner, declared.Key, payload),
            timeout.Token);
        ReadOnlyMemory<byte> stored = await new SecretStoreSourceProvider().ReadSecretAsync(
            SecretStoreTestConnections.SecretRequest("orders/api-token", _host.CreateConnection()),
            timeout.Token);

        // Assert
        observation.Status.ShouldBe("Applied");
        stored.ToArray().ShouldBe(value);
    }

    private static void ShouldChainToAnchors(ResourceCertificate certificate)
    {
        using X509Certificate2 leaf = X509Certificate2.CreateFromPem(certificate.CertificatePem);
        var bundle = new X509Certificate2Collection();
        bundle.ImportFromPem(certificate.CertificatePem);
        var anchors = new X509Certificate2Collection();
        anchors.ImportFromPem(certificate.TrustAnchorsPem);
        anchors.Count.ShouldBe(1);
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.CustomTrustStore.AddRange(anchors);
            chain.ChainPolicy.ExtraStore.AddRange(bundle);
            chain.Build(leaf).ShouldBeTrue(string.Join("; ", chain.ChainStatus.Select(status => status.StatusInformation)));
        }
        finally
        {
            foreach (X509Certificate2 item in bundle)
            {
                item.Dispose();
            }

            foreach (X509Certificate2 item in anchors)
            {
                item.Dispose();
            }
        }
    }
}
