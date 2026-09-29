using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

public sealed class SecretStoreAddSecretInputResolverTests
{
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Handles the secretstore.add-secret command kind")]
    public void CommandKind_Always_ShouldBeAddSecret()
    {
        // Arrange
        IResourceCommandInputResolver resolver = new SecretStoreAddSecretInputResolver();

        // Act
        string kind = resolver.CommandKind;

        // Assert
        kind.ShouldBe("secretstore.add-secret");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Delivers path, source, and the base64 parameter value")]
    public async Task ResolveAsync_WithParameterSource_ShouldDeliverResolvedPayload()
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve("parameter:key", Encoding.UTF8.GetBytes("command-data"));
        ResourceCommandInput declared = Declare("orders/key", """{"path":"orders/key","source":"parameter:key"}""");

        // Act
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(payload.Span)
            .ShouldBe("""{"path":"orders/key","source":"parameter:key","resolvedValue":"Y29tbWFuZC1kYXRh"}""");
        sources.Calls.ShouldBe([("parameter:key", ResourceMountKind.Secret)]);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Resolves a store source as a Secret value")]
    public async Task ResolveAsync_WithResourceSource_ShouldDeliverResolvedPayload()
    {
        // Arrange
        byte[] value = [0, 1, 2, 250, 255];
        var sources = new StubSourceResolver().Resolve("secrets:app/api-key", value);
        ResourceCommandInput declared = Declare("copied", """{"path":"copied","source":"secrets:app/api-key"}""");

        // Act
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, CancellationToken.None);

        // Assert
        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("path").GetString().ShouldBe("copied");
        document.RootElement.GetProperty("source").GetString().ShouldBe("secrets:app/api-key");
        document.RootElement.GetProperty("resolvedValue").GetBytesFromBase64().ShouldBe(value);
        sources.Calls.ShouldBe([("secrets:app/api-key", ResourceMountKind.Secret)]);
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: The delivered path is the command key")]
    public async Task ResolveAsync_WhenDeclaredPathDiffersFromKey_ShouldDeliverCommandKey()
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve("parameter:key", Encoding.UTF8.GetBytes("v"));
        ResourceCommandInput declared = Declare("key-from-command", """{"path":"declared-path","source":"parameter:key"}""");

        // Act
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, CancellationToken.None);

        // Assert
        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("path").GetString().ShouldBe("key-from-command");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Delivers an empty resolved value as an empty base64 string")]
    public async Task ResolveAsync_WithEmptyResolvedValue_ShouldDeliverEmptyBase64()
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve("parameter:empty", []);
        ResourceCommandInput declared = Declare("empty", """{"path":"empty","source":"parameter:empty"}""");

        // Act
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, CancellationToken.None);

        // Assert
        Encoding.UTF8.GetString(payload.Span)
            .ShouldBe("""{"path":"empty","source":"parameter:empty","resolvedValue":""}""");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Refuses an unbound parameter with the gateway's detail")]
    public async Task ResolveAsync_WithUnboundParameter_ShouldRefuse()
    {
        // Arrange
        var sources = new StubSourceResolver();
        ResourceCommandInput declared = Declare("key", """{"path":"key","source":"parameter:missing"}""");

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldBe("secretstore.add-secret parameter 'missing' is not bound for application 'appa'.");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Refuses an unresolved store source with the resolver's reason")]
    public async Task ResolveAsync_WithUnresolvedResourceSource_ShouldRefuseWithReason()
    {
        // Arrange
        var sources = new StubSourceResolver().Unresolve("secrets:missing", "Mount source resource 'secrets' is 'Pending', not Running.");
        ResourceCommandInput declared = Declare("key", """{"path":"key","source":"secrets:missing"}""");

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldBe(
            "secretstore.add-secret source 'secrets:missing' is unresolved: Mount source resource 'secrets' is 'Pending', not Running.");
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Refuses literal and malformed sources without resolving them")]
    [InlineData("literal:value")]
    [InlineData("LITERAL:value")]
    [InlineData("no-separator")]
    [InlineData("store:key:extra")]
    [InlineData(":key")]
    [InlineData("store:")]
    [InlineData("  :key")]
    [InlineData("store:  ")]
    public async Task ResolveAsync_WithLiteralOrMalformedSource_ShouldRefuse(string source)
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve(source, [1]);
        ResourceCommandInput declared = Declare("key", "{\"path\":\"key\",\"source\":\"" + source + "\"}");

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldBe(
            "secretstore.add-secret sources must use parameter:<name> or <resource>:<key>; literal sources are forbidden.");
        sources.Calls.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Refuses a missing or blank source")]
    [InlineData("""{"path":"key"}""")]
    [InlineData("""{"path":"key","source":""}""")]
    [InlineData("""{"path":"key","source":"   "}""")]
    [InlineData("""{"path":"key","source":null}""")]
    [InlineData("""{"path":"key","source":3}""")]
    [InlineData("""[]""")]
    public async Task ResolveAsync_WithMissingOrBlankSource_ShouldRefuse(string payload)
    {
        // Arrange
        var sources = new StubSourceResolver();
        ResourceCommandInput declared = Declare("key", payload);

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        exception.Message.ShouldBe("secretstore.add-secret requires a nonblank source.");
        sources.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Rejects a declared payload that is not JSON")]
    public async Task ResolveAsync_WithNonJsonPayload_ShouldThrowJsonException()
    {
        // Arrange
        var sources = new StubSourceResolver();
        ResourceCommandInput declared = Declare("key", "{");

        // Act / Assert
        await Should.ThrowAsync<JsonException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Rejects a command of another kind")]
    public async Task ResolveAsync_WithOtherCommandKind_ShouldThrowArgumentException()
    {
        // Arrange
        var sources = new StubSourceResolver();
        var declared = new ResourceCommandInput(
            "command-1",
            "secretstore.issue-certificate",
            "appa",
            "api",
            Encoding.UTF8.GetBytes("""{"name":"api","subject":"CN=api"}"""));

        // Act
        ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        exception.ParamName.ShouldBe("declared");
        sources.Calls.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Rejects null arguments")]
    public async Task ResolveAsync_WithNullArguments_ShouldThrowArgumentNullException()
    {
        // Arrange
        var resolver = new SecretStoreAddSecretInputResolver();
        ResourceCommandInput declared = Declare("key", """{"path":"key","source":"parameter:key"}""");

        // Act
        ArgumentNullException missingDeclared = await Should.ThrowAsync<ArgumentNullException>(
            () => resolver.ResolveAsync(null!, new StubSourceResolver(), CancellationToken.None).AsTask());
        ArgumentNullException missingSources = await Should.ThrowAsync<ArgumentNullException>(
            () => resolver.ResolveAsync(declared, null!, CancellationToken.None).AsTask());

        // Assert
        missingDeclared.ParamName.ShouldBe("declared");
        missingSources.ParamName.ShouldBe("sources");
    }

    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - AddSecretInputResolver: Observes cancellation through the source resolver")]
    public async Task ResolveAsync_WhenCancelled_ShouldThrowOperationCanceled()
    {
        // Arrange
        var sources = new StubSourceResolver().Resolve("parameter:key", [1]);
        ResourceCommandInput declared = Declare("key", """{"path":"key","source":"parameter:key"}""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, cancellation.Token).AsTask());
    }

    private static ResourceCommandInput Declare(string key, string payload) =>
        new("command-" + key, "secretstore.add-secret", "appa", key, Encoding.UTF8.GetBytes(payload));
}
