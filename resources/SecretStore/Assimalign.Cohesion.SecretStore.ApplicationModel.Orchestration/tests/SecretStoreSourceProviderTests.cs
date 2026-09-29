using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreSourceProviderTests
{
    private const string leafPem = "-----BEGIN CERTIFICATE-----\nleaf\n-----END CERTIFICATE-----\n";
    private const string rootPem = "-----BEGIN CERTIFICATE-----\nroot\n-----END CERTIFICATE-----\n";

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Declares the SecretStore resource kind")]
    public void ResourceKind_Always_ShouldBeSecretStore()
    {
        // Arrange
        IResourceSourceProvider provider = new SecretStoreSourceProvider();

        // Act
        string? kind = provider.ResourceKind;

        // Assert
        kind.ShouldBe("SecretStore");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Reads a secret from the control-plane secrets route with the bearer credential")]
    public async Task ReadSecretAsync_WithStore_ShouldGetSecretRouteWithBearerCredential()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([1, 2, 3]));
        var transports = new RecordingTransportFactory(handler);
        var provider = new SecretStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "app/api-key",
            SecretStoreTestConnections.Create());

        // Act
        ReadOnlyMemory<byte> secret = await provider.ReadSecretAsync(request, CancellationToken.None);

        // Assert
        secret.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        RecordedRequest sent = handler.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/secrets?path=app%2Fapi-key");
        sent.AuthorizationScheme.ShouldBe("Bearer");
        sent.AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        sent.Accept.ShouldBe(["application/octet-stream"]);
        sent.Body.ShouldBeEmpty();
        transports.Created.ShouldBe(1);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Hands the connection's certificate validator to the transport")]
    public async Task ReadSecretAsync_WithServerCertificateValidator_ShouldCreateTransportWithIt()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => true;
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var transports = new RecordingTransportFactory(handler);
        var provider = new SecretStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "key",
            SecretStoreTestConnections.Create(validator: validator));

        // Act
        await provider.ReadSecretAsync(request, CancellationToken.None);

        // Assert
        transports.Validators.ShouldHaveSingleItem().ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Follows the connection's control-plane path")]
    public async Task ReadSecretAsync_WithControlPlanePath_ShouldAppendRouteToIt()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "key",
            SecretStoreTestConnections.Create(address: new Uri("http://127.0.0.1:5010/prefix/cohesion/v1/")));

        // Act
        await provider.ReadSecretAsync(request, CancellationToken.None);

        // Assert
        handler.Requests.ShouldHaveSingleItem().Uri.AbsoluteUri
            .ShouldBe("http://127.0.0.1:5010/prefix/cohesion/v1/secrets?path=key");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Surfaces a store 404 as an HTTP failure carrying the status")]
    public async Task ReadSecretAsync_WhenStoreAnswersNotFound_ShouldThrowHttpRequestException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NotFound));
        var transports = new RecordingTransportFactory(handler);
        var provider = new SecretStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest("missing", SecretStoreTestConnections.Create());

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            () => provider.ReadSecretAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Refuses a request without a store connection")]
    public async Task ReadSecretAsync_WithoutStoreConnection_ShouldThrowArgumentException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest("key", store: null);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => provider.ReadSecretAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.ParamName.ShouldBe("request");
        exception.Message.ShouldContain("no connection to the store resource", Case.Sensitive);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Refuses a connection to a resource of another kind")]
    public async Task ReadSecretAsync_WithConfigurationStoreConnection_ShouldThrowArgumentException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "key",
            SecretStoreTestConnections.Create(kind: "ConfigurationStore"));

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => provider.ReadSecretAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain("kind 'ConfigurationStore'", Case.Sensitive);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Accepts the SecretStore kind case-insensitively")]
    public async Task ReadSecretAsync_WithLowercaseKind_ShouldRead()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "key",
            SecretStoreTestConnections.Create(kind: "secretstore"));

        // Act
        ReadOnlyMemory<byte> secret = await provider.ReadSecretAsync(request, CancellationToken.None);

        // Assert
        secret.ToArray().ShouldBe(new byte[] { 7 });
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Supplies Secret mounts only")]
    public async Task ReadSecretAsync_ForConfigurationMount_ShouldThrowNotSupported()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes([7]));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "key",
            SecretStoreTestConnections.Create(),
            ResourceMountKind.Configuration);

        // Act
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
            () => provider.ReadSecretAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain("Secret mounts only", Case.Sensitive);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Rejects a null request")]
    public async Task ReadSecretAsync_WithNullRequest_ShouldThrowArgumentNullException()
    {
        // Arrange
        var provider = new SecretStoreSourceProvider();

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(
            () => provider.ReadSecretAsync(null!, CancellationToken.None).AsTask());

        // Assert
        exception.ParamName.ShouldBe("request");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Reads the certificate, then the store's ca/root anchors")]
    public async Task ReadCertificateAsync_WithStore_ShouldReadCertificateThenRoot()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(request =>
            RecordingHttpMessageHandler.Pem(request.Uri.Query.Contains("ca%2Froot", StringComparison.Ordinal) ? rootPem : leafPem));
        var transports = new RecordingTransportFactory(handler);
        var provider = new SecretStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "certs/appa-api",
            SecretStoreTestConnections.Create());

        // Act
        ResourceCertificate certificate = await provider.ReadCertificateAsync(request, CancellationToken.None);

        // Assert
        certificate.CertificatePem.ShouldBe(leafPem);
        certificate.TrustAnchorsPem.ShouldBe(rootPem);
        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/certificates?name=certs%2Fappa-api");
        handler.Requests[0].Accept.ShouldBe(["application/x-pem-file"]);
        handler.Requests[0].AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        handler.Requests[1].Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/certificates?name=ca%2Froot");
        handler.Requests[1].AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        transports.Created.ShouldBe(1);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Fails the certificate read when the root is unavailable")]
    public async Task ReadCertificateAsync_WhenRootReadFails_ShouldThrowHttpRequestException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(request =>
            request.Uri.Query.Contains("ca%2Froot", StringComparison.Ordinal)
                ? RecordingHttpMessageHandler.Status(HttpStatusCode.ServiceUnavailable)
                : RecordingHttpMessageHandler.Pem(leafPem));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest("certs/appa-api", SecretStoreTestConnections.Create());

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            () => provider.ReadCertificateAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Rejects an empty certificate without reading the root")]
    public async Task ReadCertificateAsync_WhenStoreReturnsEmptyCertificate_ShouldThrowInvalidDataException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(string.Empty));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest("certs/appa-api", SecretStoreTestConnections.Create());

        // Act
        await Should.ThrowAsync<InvalidDataException>(
            () => provider.ReadCertificateAsync(request, CancellationToken.None).AsTask());

        // Assert
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Refuses a certificate request without a store connection")]
    public async Task ReadCertificateAsync_WithoutStoreConnection_ShouldThrowArgumentException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest("certs/appa-api", store: null);

        // Act
        await Should.ThrowAsync<ArgumentException>(
            () => provider.ReadCertificateAsync(request, CancellationToken.None).AsTask());

        // Assert
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Supplies certificates for Secret mounts only")]
    public async Task ReadCertificateAsync_ForConfigurationMount_ShouldThrowNotSupported()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Pem(leafPem));
        var provider = new SecretStoreSourceProvider(new RecordingTransportFactory(handler).Create);
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "certs/appa-api",
            SecretStoreTestConnections.Create(),
            ResourceMountKind.Configuration);

        // Act
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
            () => provider.ReadCertificateAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain("Secret mounts only", Case.Sensitive);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Does not supply Configuration mounts")]
    public async Task ReadConfigurationAsync_Always_ShouldThrowNotSupported()
    {
        // Arrange
        IResourceSourceProvider provider = new SecretStoreSourceProvider();
        ResourceSourceRequest request = SecretStoreTestConnections.SecretRequest(
            "settings",
            SecretStoreTestConnections.Create(),
            ResourceMountKind.Configuration);

        // Act
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
            () => provider.ReadConfigurationAsync(request, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain(nameof(SecretStoreSourceProvider), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Requires a transport factory")]
    public void Constructor_WithNullTransportFactory_ShouldThrowArgumentNullException()
    {
        // Arrange
        Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> factory = null!;

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => new SecretStoreSourceProvider(factory));

        // Assert
        exception.ParamName.ShouldBe("transportFactory");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - SourceProvider: Observes cancellation")]
    public async Task ReadSecretAsync_WhenCancelled_ShouldThrowOperationCanceled()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes(Encoding.UTF8.GetBytes("value")));
        var transports = new RecordingTransportFactory(handler);
        var provider = new SecretStoreSourceProvider(transports.Create);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => provider.ReadSecretAsync(
                SecretStoreTestConnections.SecretRequest("key", SecretStoreTestConnections.Create()),
                cancellation.Token)
            .AsTask());

        // Assert
        transports.Disposed.ShouldBe(transports.Created);
    }
}
