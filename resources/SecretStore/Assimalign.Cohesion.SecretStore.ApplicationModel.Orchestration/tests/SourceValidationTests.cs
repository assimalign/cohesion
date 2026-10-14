using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration.Tests;

/// <summary>Compatibility tests for the case-sensitive parameter-source branch.</summary>
public sealed class SourceValidationTests
{
    /// <summary>Only the exact parameter prefix permits another colon in its name.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - ResolveAsync: preserves valid source spelling")]
    [InlineData("parameter:name:part")]
    [InlineData("Parameter:name")]
    public async Task ResolveAsync_ValidSource_ShouldPreserveSpelling(string source)
    {
        // Arrange
        byte[] value = [1, 2, 3];
        var sources = new StubSourceResolver().Resolve(source, value);
        ResourceCommandInput declared = Declare(source);

        // Act
        ReadOnlyMemory<byte> payload = await new SecretStoreAddSecretInputResolver()
            .ResolveAsync(declared, sources, CancellationToken.None);

        // Assert
        using JsonDocument document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("source").GetString().ShouldBe(source);
        document.RootElement.GetProperty("resolvedValue").GetBytesFromBase64().ShouldBe(value);
        sources.Calls.ShouldBe([(source, ResourceMountKind.Secret)]);
    }

    /// <summary>A differently cased parameter prefix remains a resource source with exactly one colon.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel.Orchestration] - ResolveAsync: rejects additional separator for uppercase resource prefix")]
    public async Task ResolveAsync_UppercaseParameterWithAdditionalSeparator_ShouldRejectBeforeResolution()
    {
        // Arrange
        const string source = "Parameter:name:part";
        var sources = new StubSourceResolver().Resolve(source, [1]);
        ResourceCommandInput declared = Declare(source);

        // Act
        await Should.ThrowAsync<InvalidOperationException>(() =>
            new SecretStoreAddSecretInputResolver().ResolveAsync(declared, sources, CancellationToken.None).AsTask());

        // Assert
        sources.Calls.ShouldBeEmpty();
    }

    private static ResourceCommandInput Declare(string source) =>
        new("command-key", "secretstore.add-secret", "appa", "key",
            Encoding.UTF8.GetBytes("{\"path\":\"key\",\"source\":\"" + source + "\"}"));
}
