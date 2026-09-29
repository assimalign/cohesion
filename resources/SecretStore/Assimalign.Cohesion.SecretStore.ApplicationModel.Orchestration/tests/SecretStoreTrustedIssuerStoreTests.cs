using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreTrustedIssuerStoreTests
{
    private const string owner = "appa@local";

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Declares the SecretStore resource kind")]
    public void ResourceKind_Always_ShouldBeSecretStore()
    {
        // Arrange
        ITrustedIssuerStore store = new SecretStoreTrustedIssuerStore();

        // Act
        string? kind = store.ResourceKind;

        // Assert
        kind.ShouldBe("SecretStore");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Reads and parses the exported trusted-issuers.json secret")]
    public async Task ReadAsync_WithExportedDocument_ShouldGetTrustedIssuersSecretAndParse()
    {
        // Arrange
        using var first = new TestTrustIdentity("peer-a");
        using var second = new TestTrustIdentity("peer-b");
        byte[] document = Encoding.UTF8.GetBytes(
            "{\"issuers\":[" +
            "{\"owner\":\"appa@local\",\"issuer\":\"peer-a\",\"trustKey\":" + Encoding.UTF8.GetString(first.PublicKey.Span) + "}," +
            "{\"owner\":\"appa@local\",\"issuer\":\"peer-b\",\"trustKey\":" + Encoding.UTF8.GetString(second.PublicKey.Span) +
            ",\"allowedCommandKinds\":[\"web.add-route\"]}]}");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes(document));
        var transports = new RecordingTransportFactory(handler);
        var store = new SecretStoreTrustedIssuerStore(transports.Create);

        // Act
        IReadOnlyList<TrustedIssuer>? issuers = await store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None);

        // Assert
        issuers.ShouldNotBeNull();
        issuers.Count.ShouldBe(2);
        issuers[0].Issuer.ShouldBe("peer-a");
        issuers[0].PublicKey.GetRawText().ShouldBe(Encoding.UTF8.GetString(first.PublicKey.Span));
        issuers[0].AllowedCommandKinds.ShouldBeEmpty();
        issuers[1].Issuer.ShouldBe("peer-b");
        issuers[1].AllowedCommandKinds.ShouldBe(["web.add-route"]);
        RecordedRequest sent = handler.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Get);
        sent.Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/secrets?path=trusted-issuers.json");
        sent.AuthorizationScheme.ShouldBe("Bearer");
        sent.AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: A 404 means nothing is persisted yet")]
    public async Task ReadAsync_WhenStoreAnswersNotFound_ShouldReturnNull()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NotFound));
        var transports = new RecordingTransportFactory(handler);
        var store = new SecretStoreTrustedIssuerStore(transports.Create);

        // Act
        IReadOnlyList<TrustedIssuer>? issuers = await store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None);

        // Assert
        issuers.ShouldBeNull();
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: An empty issuers array is an empty list, not absence")]
    public async Task ReadAsync_WithEmptyDocument_ShouldReturnEmptyList()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes("{\"issuers\":[]}"u8.ToArray()));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        IReadOnlyList<TrustedIssuer>? issuers = await store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None);

        // Assert
        issuers.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Refusals other than 404 surface as HTTP failures")]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReadAsync_WhenStoreRefuses_ShouldThrowHttpRequestException(HttpStatusCode status)
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(status));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(
            () => store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None).AsTask());

        // Assert
        exception.StatusCode.ShouldBe(status);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Rejects a malformed trusted-issuers document")]
    [InlineData("[]", "an 'issuers' array")]
    [InlineData("{\"issuers\":{}}", "an 'issuers' array")]
    [InlineData("{\"issuers\":[{\"issuer\":\"peer\"}]}", "string 'issuer' and object 'trustKey'")]
    [InlineData("{\"issuers\":[{\"issuer\":3,\"trustKey\":{}}]}", "string 'issuer' and object 'trustKey'")]
    [InlineData("{\"issuers\":[{\"issuer\":\"peer\",\"trustKey\":{\"kty\":\"RSA\"}}]}", "has an invalid public key")]
    public async Task ReadAsync_WithMalformedDocument_ShouldThrowInvalidDataException(string document, string expected)
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes(Encoding.UTF8.GetBytes(document)));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Rejects a document that repeats an issuer")]
    public async Task ReadAsync_WithDuplicateIssuer_ShouldThrowInvalidDataException()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        string key = Encoding.UTF8.GetString(peer.PublicKey.Span);
        byte[] document = Encoding.UTF8.GetBytes(
            "{\"issuers\":[{\"issuer\":\"peer\",\"trustKey\":" + key + "},{\"issuer\":\"peer\",\"trustKey\":" + key + "}]}");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes(document));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldBe("Trusted issuer 'peer' occurs more than once in the document.");
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Rejects an invalid command-kind restriction")]
    [InlineData("\"web.add-route\"", "must be a string array")]
    [InlineData("[\"\"]", "requires nonblank strings")]
    [InlineData("[3]", "requires nonblank strings")]
    public async Task ReadAsync_WithInvalidAllowedCommandKinds_ShouldThrowInvalidDataException(string kinds, string expected)
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        byte[] document = Encoding.UTF8.GetBytes(
            "{\"issuers\":[{\"issuer\":\"peer\",\"trustKey\":" + Encoding.UTF8.GetString(peer.PublicKey.Span) +
            ",\"allowedCommandKinds\":" + kinds + "}]}");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes(document));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        InvalidDataException exception = await Should.ThrowAsync<InvalidDataException>(
            () => store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Rejects content that is not JSON")]
    public async Task ReadAsync_WithNonJsonContent_ShouldThrowJsonException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Bytes("not json"u8.ToArray()));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act / Assert
        await Should.ThrowAsync<JsonException>(
            () => store.ReadAsync(SecretStoreTestConnections.Create(), CancellationToken.None).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Read requires a connection to the bound store")]
    public async Task ReadAsync_WithoutStoreConnection_ShouldThrowArgumentNullException()
    {
        // Arrange
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NotFound));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentNullException exception = await Should.ThrowAsync<ArgumentNullException>(
            () => store.ReadAsync(store: null, CancellationToken.None).AsTask());

        // Assert
        exception.ParamName.ShouldBe("store");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Sends cohesion.trust.add with the raw key payload and the derived trust id")]
    public async Task AddAsync_WithUnrestrictedIssuer_ShouldPostTrustAddCommand()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        TrustedIssuer issuer = peer.ToTrustedIssuer();
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var transports = new RecordingTransportFactory(handler);
        var store = new SecretStoreTrustedIssuerStore(transports.Create);
        byte[] expectedPayload = peer.PublicKey.ToArray();

        // Act
        await store.AddAsync(SecretStoreTestConnections.Create(), owner, issuer, CancellationToken.None);

        // Assert
        RecordedRequest sent = handler.Requests.ShouldHaveSingleItem();
        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Uri.AbsoluteUri.ShouldBe("https://secrets.test:8443/cohesion/v1/commands");
        sent.AuthorizationScheme.ShouldBe("Bearer");
        sent.AuthorizationParameter.ShouldBe(SecretStoreTestConnections.BearerCredential);
        sent.ContentType.ShouldBe("application/json");
        using JsonDocument body = JsonDocument.Parse(sent.Body);
        body.RootElement.GetProperty("id").GetString().ShouldBe(ExpectedTrustId(owner, "peer", expectedPayload));
        body.RootElement.GetProperty("kind").GetString().ShouldBe("cohesion.trust.add");
        body.RootElement.GetProperty("owner").GetString().ShouldBe(owner);
        body.RootElement.GetProperty("key").GetString().ShouldBe("peer");
        body.RootElement.GetProperty("payload").GetBytesFromBase64().ShouldBe(expectedPayload);
        transports.Disposed.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: A restricted grant sends the trustKey/allowedCommandKinds envelope")]
    public async Task AddAsync_WithRestrictedIssuer_ShouldPostEnvelopePayload()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        TrustedIssuer issuer = peer.ToTrustedIssuer("web.add-route", "database.create-login", "web.add-route");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);
        byte[] expectedPayload = Encoding.UTF8.GetBytes(
            "{\"trustKey\":" + Encoding.UTF8.GetString(peer.PublicKey.Span) +
            ",\"allowedCommandKinds\":[\"database.create-login\",\"web.add-route\"]}");

        // Act
        await store.AddAsync(SecretStoreTestConnections.Create(), owner, issuer, CancellationToken.None);

        // Assert
        using JsonDocument body = JsonDocument.Parse(handler.Requests.ShouldHaveSingleItem().Body);
        body.RootElement.GetProperty("payload").GetBytesFromBase64().ShouldBe(expectedPayload);
        body.RootElement.GetProperty("id").GetString().ShouldBe(ExpectedTrustId(owner, "peer", expectedPayload));
        body.RootElement.GetProperty("key").GetString().ShouldBe("peer");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: An identical grant carries the same command id")]
    public async Task AddAsync_SameGrantTwice_ShouldSendSameCommandId()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        await store.AddAsync(SecretStoreTestConnections.Create(), owner, peer.ToTrustedIssuer(), CancellationToken.None);
        await store.AddAsync(SecretStoreTestConnections.Create(), owner, peer.ToTrustedIssuer(), CancellationToken.None);
        await store.AddAsync(SecretStoreTestConnections.Create(), "appa@other", peer.ToTrustedIssuer(), CancellationToken.None);

        // Assert
        handler.Requests.Count.ShouldBe(3);
        string first = ReadCommandId(handler.Requests[0]);
        ReadCommandId(handler.Requests[1]).ShouldBe(first);
        ReadCommandId(handler.Requests[2]).ShouldNotBe(first);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Store refusals of a grant surface as HTTP failures")]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task AddAsync_WhenStoreRefuses_ShouldThrowHttpRequestException(HttpStatusCode status)
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(status));
        var transports = new RecordingTransportFactory(handler);
        var store = new SecretStoreTrustedIssuerStore(transports.Create);

        // Act
        HttpRequestException exception = await Should.ThrowAsync<HttpRequestException>(() => store.AddAsync(
                SecretStoreTestConnections.Create(),
                owner,
                peer.ToTrustedIssuer(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.StatusCode.ShouldBe(status);
        transports.Disposed.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: A grant requires an owner")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AddAsync_WithBlankOwner_ShouldThrowArgumentException(string blank)
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => store.AddAsync(
                SecretStoreTestConnections.Create(),
                blank,
                peer.ToTrustedIssuer(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("owner");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: A grant requires an issuer and a store connection")]
    public async Task AddAsync_WithNullIssuerOrStore_ShouldThrowArgumentNullException()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentNullException missingIssuer = await Should.ThrowAsync<ArgumentNullException>(() => store.AddAsync(
                SecretStoreTestConnections.Create(), owner, null!, CancellationToken.None).AsTask());
        ArgumentNullException missingStore = await Should.ThrowAsync<ArgumentNullException>(() => store.AddAsync(
                null, owner, peer.ToTrustedIssuer(), CancellationToken.None).AsTask());

        // Assert
        missingIssuer.ParamName.ShouldBe("issuer");
        missingStore.ParamName.ShouldBe("store");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Refuses a connection to a resource of another kind")]
    public async Task AddAsync_WithNonSecretStoreConnection_ShouldThrowArgumentException()
    {
        // Arrange
        using var peer = new TestTrustIdentity("peer");
        var handler = new RecordingHttpMessageHandler(_ => RecordingHttpMessageHandler.Status(HttpStatusCode.NoContent));
        var store = new SecretStoreTrustedIssuerStore(new RecordingTransportFactory(handler).Create);

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(() => store.AddAsync(
                SecretStoreTestConnections.Create(kind: "ConfigurationStore"),
                owner,
                peer.ToTrustedIssuer(),
                CancellationToken.None)
            .AsTask());

        // Assert
        exception.ParamName.ShouldBe("store");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - TrustedIssuerStore: Requires a transport factory")]
    public void Constructor_WithNullTransportFactory_ShouldThrowArgumentNullException()
    {
        // Arrange
        Func<RemoteCertificateValidationCallback?, HttpMessageInvoker> factory = null!;

        // Act
        ArgumentNullException exception = Should.Throw<ArgumentNullException>(() => new SecretStoreTrustedIssuerStore(factory));

        // Assert
        exception.ParamName.ShouldBe("transportFactory");
    }

    // The derivation the gateway's store client used: trust- + lowercase hex SHA-256 of
    // UTF-8 "owner\nissuer\n" followed by the payload bytes.
    private static string ExpectedTrustId(string grantOwner, string issuer, byte[] payload)
    {
        byte[] identity = Encoding.UTF8.GetBytes(grantOwner + "\n" + issuer + "\n");
        byte[] hashed = new byte[identity.Length + payload.Length];
        identity.CopyTo(hashed, 0);
        payload.CopyTo(hashed, identity.Length);
        return "trust-" + Convert.ToHexStringLower(SHA256.HashData(hashed));
    }

    private static string ReadCommandId(RecordedRequest request)
    {
        using JsonDocument body = JsonDocument.Parse(request.Body);
        return body.RootElement.GetProperty("id").GetString()!;
    }
}
