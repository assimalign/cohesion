using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreCertificateAuthorityTests
{
    private const string leafPem = "-----BEGIN CERTIFICATE-----\nleaf\n-----END CERTIFICATE-----\n";
    private const string rootPem = "-----BEGIN CERTIFICATE-----\nroot\n-----END CERTIFICATE-----\n";

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Declares the SecretStore resource kind")]
    public void ResourceKind_Always_ShouldBeSecretStore()
    {
        // Arrange
        IResourceCertificateAuthority authority = new SecretStoreCertificateAuthority();

        // Act
        string? kind = authority.ResourceKind;

        // Assert
        kind.ShouldBe("SecretStore");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Resolves certs/<leaf>, then the ca/root anchors")]
    public async Task IssueAsync_WithAuthority_ShouldReadLeafNamespaceThenRoot()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => true;
        var handler = new RecordingHttpMessageHandler(request =>
            RecordingHttpMessageHandler.Pem(request.Uri.Query.Contains("ca%2Froot", StringComparison.Ordinal) ? rootPem : leafPem));
        var transports = new RecordingTransportFactory(handler);
        var authority = new SecretStoreCertificateAuthority(transports.Create);

        // Act
        ResourceCertificate certificate = await authority.IssueAsync(
            SecretStoreTestConnections.CertificateRequest("api-https"),
            SecretStoreTestConnections.Create(validator: validator),
            CancellationToken.None);

        // Assert
        certificate.CertificatePem.ShouldBe(leafPem);
        certificate.TrustAnchorsPem.ShouldBe(rootPem);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/certificates?name=certs%2Fapi-https");
        handler.Requests[0].AuthorizationScheme.ShouldBe("Bearer");
        handler.Requests[0].AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        handler.Requests[0].Accept.ShouldBe(["application/x-pem-file"]);
        handler.Requests[1].Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/certificates?name=ca%2Froot");
        transports.Validators.ShouldHaveSingleItem().ShouldBeSameAs(validator);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Does not forward subject alternative names to the store")]
    public async Task IssueAsync_WithSubjectAlternativeNames_ShouldNotSendThem()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var authority = new SecretStoreCertificateAuthority(new RecordingTransportFactory(handler).Create);
        ResourceCertificateRequest request = SecretStoreTestConnections.CertificateRequest("api-https");

        // Act
        await authority.IssueAsync(request, SecretStoreTestConnections.Create(), CancellationToken.None);

        // Assert
        foreach (RecordedRequest sent in handler.Requests)
        {
            sent.Uri.Query.ShouldNotContain("api.appa.internal", Case.Sensitive);
            sent.Body.ShouldBeEmpty();
        }
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Requires a connection to the bound store")]
    public async Task IssueAsync_WithoutAuthorityConnection_ShouldThrowArgumentNullException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var authority = new SecretStoreCertificateAuthority(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(() => authority.IssueAsync(
                SecretStoreTestConnections.CertificateRequest(),
                authority: null,
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("authority");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Refuses a connection to a resource of another kind")]
    public async Task IssueAsync_WithNonSecretStoreConnection_ShouldThrowArgumentException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var authority = new SecretStoreCertificateAuthority(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => authority.IssueAsync(
                SecretStoreTestConnections.CertificateRequest(),
                SecretStoreTestConnections.Create(kind: "Web"),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("authority");
        handler.Requests.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Requires a leaf name")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task IssueAsync_WithBlankLeafName_ShouldThrowArgumentException(string leafName)
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var authority = new SecretStoreCertificateAuthority(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => authority.IssueAsync(
                SecretStoreTestConnections.CertificateRequest(leafName),
                SecretStoreTestConnections.Create(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("request");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Rejects a null request")]
    public async Task IssueAsync_WithNullRequest_ShouldThrowArgumentNullException()
    {
        // Arrange
        var authority = new SecretStoreCertificateAuthority();

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(() => authority.IssueAsync(
                null!,
                SecretStoreTestConnections.Create(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("request");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Surfaces a store refusal as an HTTP failure")]
    public async Task IssueAsync_WhenStoreRefuses_ShouldThrowHttpRequestException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NotImplemented));
        var transports = new RecordingTransportFactory(handler);
        var authority = new SecretStoreCertificateAuthority(transports.Create);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => authority.IssueAsync(
                SecretStoreTestConnections.CertificateRequest(),
                SecretStoreTestConnections.Create(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        handler.Requests.ShouldHaveSingleItem();
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Fails the issue when the ca/root anchors are unavailable")]
    public async Task IssueAsync_WhenRootReadFails_ShouldThrowHttpRequestException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(request =>
            request.Uri.Query.Contains("ca%2Froot", StringComparison.Ordinal)
                ? RecordingHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable)
                : RecordingHttpMessageHandler.Pem(leafPem));
        var transports = new RecordingTransportFactory(handler);
        var authority = new SecretStoreCertificateAuthority(transports.Create);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => authority.IssueAsync(
                SecretStoreTestConnections.CertificateRequest(),
                SecretStoreTestConnections.Create(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        handler.Requests.Count.ShouldBe(2);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - CertificateAuthority: Requires a transport factory")]
    public void Constructor_WithNullTransportFactory_ShouldThrowArgumentNullException()
    {
        // Arrange
        Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> factory = null!;

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => new SecretStoreCertificateAuthority(factory));

        // Assert
        exception.ParamName.ShouldBe("transportFactory");
    }
}
