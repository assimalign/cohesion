using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ConfigurationStore.Client;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

public sealed class ConfigurationStoreSourceProviderTests
{
    private const string namespaceDocument = "{\"Mode\":\"production\",\"Optional\":null,\"Endpoint\":\"https://api.test\"}";

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ResourceKind: Declares the ConfigurationStore manifest kind")]
    public void ResourceKind_Default_ShouldBeConfigurationStore()
    {
        // Arrange
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();

        // Act
        string? kind = provider.ResourceKind;

        // Assert
        kind.ShouldBe("ConfigurationStore");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Sends one authenticated namespace read to the store root")]
    public async Task ReadConfigurationAsync_WithStoreConnection_ShouldSendAuthenticatedNamespaceRead()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());

        // Act
        await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        RecordedRequest sent = transports.Handlers.ShouldHaveSingleItem().Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.RequestUri.AbsoluteUri.ShouldBe("https://configuration.test:8443/cohesion/v1/namespaces?name=api");
        sent.Authorization.ShouldBe("Bearer store-token");
        sent.Accept.ShouldBe("application/json");
        sent.HasContent.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Sends exactly the request the gateway store read sent")]
    public async Task ReadConfigurationAsync_ComparedWithGatewayStoreRead_ShouldSendIdenticalRequest()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(), key: "apps/api");

        // The gateway read the observed endpoint root (no path) through ConfigurationStoreClient.Create.
        var legacyHandler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        using var legacyTransport = new HttpMessageInvoker(legacyHandler, disposeHandler: true);
        IConfigurationStoreClient legacy = ConfigurationStoreClient.Create(
            Uri.CreateEndpoint("https", "configuration.test", 8443),
            new ClientCredential(SourceRequestFactory.Credential),
            legacyTransport);

        // Act
        IReadOnlyDictionary<string, string?> expected = await legacy.GetNamespaceAsync("apps/api", CancellationToken.None);
        IReadOnlyDictionary<string, string?> actual = await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        RecordedRequest legacyRequest = legacyHandler.Requests.ShouldHaveSingleItem();
        RecordedRequest providerRequest = transports.Handlers.ShouldHaveSingleItem().Requests.ShouldHaveSingleItem();
        providerRequest.Method.ShouldBe(legacyRequest.Method);
        providerRequest.RequestUri.AbsoluteUri.ShouldBe(legacyRequest.RequestUri.AbsoluteUri);
        providerRequest.Authorization.ShouldBe(legacyRequest.Authorization);
        providerRequest.Accept.ShouldBe(legacyRequest.Accept);
        providerRequest.HasContent.ShouldBe(legacyRequest.HasContent);
        actual.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Returns every namespace entry, null values included")]
    public async Task ReadConfigurationAsync_WithStoreConnection_ShouldReturnNamespaceValues()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());

        // Act
        IReadOnlyDictionary<string, string?> values = await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        values.Count.ShouldBe(3);
        values["Mode"].ShouldBe("production");
        values.ContainsKey("Optional").ShouldBeTrue();
        values["Optional"].ShouldBeNull();
        values["Endpoint"].ShouldBe("https://api.test");
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Reads the namespace route from the control-plane endpoint root")]
    [InlineData("https://configuration.test:8443/cohesion/v1", "https://configuration.test:8443/cohesion/v1/namespaces?name=api")]
    [InlineData("https://configuration.test:8443/custom/control", "https://configuration.test:8443/cohesion/v1/namespaces?name=api")]
    [InlineData("https://configuration.test:8443", "https://configuration.test:8443/cohesion/v1/namespaces?name=api")]
    [InlineData("http://127.0.0.1:8080/cohesion/v1", "http://127.0.0.1:8080/cohesion/v1/namespaces?name=api")]
    [InlineData("https://[::1]:8443/cohesion/v1", "https://[::1]:8443/cohesion/v1/namespaces?name=api")]
    public async Task ReadConfigurationAsync_WithControlPlaneAddress_ShouldReadFromEndpointRoot(string address, string expected)
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(address));

        // Act
        await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        transports.Handlers.ShouldHaveSingleItem().Requests.ShouldHaveSingleItem()
            .RequestUri.AbsoluteUri.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Query-escapes the namespace name")]
    public async Task ReadConfigurationAsync_WithSlashInKey_ShouldEscapeNamespaceName()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(), key: "apps/api v2");

        // Act
        await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        transports.Handlers.ShouldHaveSingleItem().Requests.ShouldHaveSingleItem()
            .RequestUri.AbsoluteUri.ShouldBe("https://configuration.test:8443/cohesion/v1/namespaces?name=apps%2Fapi%20v2");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Hands the connection's certificate validator to the transport")]
    public async Task ReadConfigurationAsync_WithServerCertificateValidator_ShouldHandValidatorToTransport()
    {
        // Arrange
        RemoteCertificateValidationCallback validator = static (_, _, _, _) => true;
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(validator: validator));

        // Act
        await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        transports.Validators.ShouldHaveSingleItem().ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Uses a new transport per read and disposes it")]
    public async Task ReadConfigurationAsync_TwoReads_ShouldDisposeEachTransport()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());

        // Act
        await provider.ReadConfigurationAsync(request, CancellationToken.None);
        await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        transports.Handlers.Count.ShouldBe(2);
        transports.Handlers.ShouldAllBe(handler => handler.IsDisposed);
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: A refused read surfaces the store's HTTP status")]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReadConfigurationAsync_WhenStoreRefuses_ShouldThrowHttpRequestExceptionWithStatus(HttpStatusCode status)
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => new HttpResponseMessage(status));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            async () => await provider.ReadConfigurationAsync(request, CancellationToken.None));

        // Assert
        exception.StatusCode.ShouldBe(status);
        transports.Handlers.ShouldHaveSingleItem().IsDisposed.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: A null or malformed namespace document is a JSON failure")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[\"not\",\"an\",\"object\"]")]
    public async Task ReadConfigurationAsync_WithInvalidDocument_ShouldThrowJsonException(string document)
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, document));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<JsonException>(read);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Refuses a Secret mount without sending a request")]
    public async Task ReadConfigurationAsync_WithSecretMountKind_ShouldThrowNotSupportedWithoutRequest()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(),
            kind: ResourceMountKind.Secret);

        // Act
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
            async () => await provider.ReadConfigurationAsync(request, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("Mount 'settings' on resource 'api' is 'Secret'", Case.Sensitive);
        transports.Handlers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Refuses a request without a store connection")]
    public async Task ReadConfigurationAsync_WithoutStoreConnection_ShouldThrowArgumentWithoutRequest()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(store: null);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await provider.ReadConfigurationAsync(request, CancellationToken.None));

        // Assert
        exception.ParamName.ShouldBe("request");
        exception.Message.ShouldContain("no connection to the store resource", Case.Sensitive);
        transports.Handlers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Refuses a connection to a resource of another kind")]
    public async Task ReadConfigurationAsync_WithOtherResourceKind_ShouldThrowArgumentWithoutRequest()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(resourceKind: "SecretStore"));

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            async () => await provider.ReadConfigurationAsync(request, CancellationToken.None));

        // Assert
        exception.Message.ShouldContain("of kind 'SecretStore'", Case.Sensitive);
        transports.Handlers.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Resource kinds compare case-insensitively")]
    public async Task ReadConfigurationAsync_WithDifferentKindCase_ShouldRead()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(resourceKind: "configurationstore"));

        // Act
        IReadOnlyDictionary<string, string?> values = await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        values["Mode"].ShouldBe("production");
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Rejects a null request")]
    public async Task ReadConfigurationAsync_WithNullRequest_ShouldThrowArgumentNull()
    {
        // Arrange
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(null!, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentNullException>(read);
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Rejects a blank namespace before sending")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReadConfigurationAsync_WithBlankKey_ShouldThrowArgumentWithoutRequest(string key)
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(), key: key);

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(read);
        transports.Handlers.ShouldHaveSingleItem().Requests.ShouldBeEmpty();
        transports.Handlers[0].IsDisposed.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Rejects a blank bearer credential before sending")]
    public async Task ReadConfigurationAsync_WithBlankCredential_ShouldThrowArgumentWithoutRequest()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(credential: " "));

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(read);
        transports.Handlers.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Rejects a control-plane address that is not an HTTP(S) endpoint")]
    [InlineData("tcp://configuration.test:8443/cohesion/v1")]
    [InlineData("https://configuration.test:8443/cohesion/v1?x=1")]
    [InlineData("https://user@configuration.test:8443/cohesion/v1")]
    public async Task ReadConfigurationAsync_WithInvalidControlPlaneAddress_ShouldThrowArgumentWithoutRequest(string address)
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection(address));

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(request, CancellationToken.None);

        // Assert
        await Should.ThrowAsync<ArgumentException>(read);
        transports.Handlers.ShouldAllBe(handler => handler.Requests.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadConfiguration: Honors cancellation")]
    public async Task ReadConfigurationAsync_WithCancelledToken_ShouldThrowOperationCanceled()
    {
        // Arrange
        var transports = new RecordingTransportFactory(_ => RecordingHttpMessageHandler.Json(HttpStatusCode.OK, namespaceDocument));
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider(transports.Create);
        ResourceSourceRequest request = SourceRequestFactory.Request(SourceRequestFactory.Connection());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        Func<Task> read = async () => await provider.ReadConfigurationAsync(request, cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(read);
        transports.Handlers.ShouldHaveSingleItem().Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadSecret: Stays unsupported")]
    public async Task ReadSecretAsync_AnyRequest_ShouldThrowNotSupported()
    {
        // Arrange
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(),
            kind: ResourceMountKind.Secret);

        // Act
        Func<Task> read = async () => await provider.ReadSecretAsync(request, CancellationToken.None);

        // Assert
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(read);
        exception.Message.ShouldContain(nameof(ConfigurationStoreSourceProvider), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - ReadCertificate: Stays unsupported")]
    public async Task ReadCertificateAsync_AnyRequest_ShouldThrowNotSupported()
    {
        // Arrange
        IResourceSourceProvider provider = new ConfigurationStoreSourceProvider();
        ResourceSourceRequest request = SourceRequestFactory.Request(
            SourceRequestFactory.Connection(),
            kind: ResourceMountKind.Secret);

        // Act
        Func<Task> read = async () => await provider.ReadCertificateAsync(request, CancellationToken.None);

        // Assert
        NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(read);
        exception.Message.ShouldContain(nameof(ConfigurationStoreSourceProvider), Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [ConfigurationStore.ApplicationModel.Orchestration] - Constructor: Rejects a null transport factory")]
    public void Constructor_WithNullTransportFactory_ShouldThrowArgumentNull()
    {
        // Arrange
        Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> factory = null!;

        // Act
        Action create = () => _ = new ConfigurationStoreSourceProvider(factory);

        // Assert
        Should.Throw<ArgumentNullException>(create);
    }
}
