using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

/// <summary>Regressions for command values that previously failed only during delivery.</summary>
public sealed class CommandCompositionRegressionTests
{
    /// <summary>A resource source must contain exactly one separator before delivery starts.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AddSecret: rejects extra source separators at composition")]
    public void AddSecret_ExtraSourceSeparator_ShouldRejectAtComposition()
    {
        // Arrange
        ISecretStoreResourceDescriptor descriptor = CreateDescriptor();

        // Act
        Action declare = () => descriptor.AddSecret("orders/key", "store:key:extra");

        // Assert
        Should.Throw<ArgumentException>(declare);
    }

    /// <summary>Commands cannot claim trust or certificate storage paths.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AddSecret: rejects reserved write paths at composition")]
    [InlineData("trusted-issuers.json")]
    [InlineData("certs/api")]
    [InlineData("certs/")]
    public void AddSecret_ReservedPath_ShouldRejectAtComposition(string path)
    {
        // Arrange
        ISecretStoreResourceDescriptor descriptor = CreateDescriptor();

        // Act
        Action declare = () => descriptor.AddSecret(path, "parameter:credential");

        // Assert
        Should.Throw<ArgumentException>(declare);
    }

    /// <summary>Leaf storage names cannot be dot segments or contain backslashes.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - IssueCertificate: rejects malformed leaf names at composition")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("\\")]
    [InlineData("api\\leaf")]
    public void IssueCertificate_InvalidLeafName_ShouldRejectAtComposition(string name)
    {
        // Arrange
        ISecretStoreResourceDescriptor descriptor = CreateDescriptor();

        // Act
        Action declare = () => descriptor.IssueCertificate(name, "CN=api");

        // Assert
        Should.Throw<ArgumentException>(declare);
    }

    /// <summary>Malformed certificate DNS input is refused before BCL issuance.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - IssueCertificate: rejects malformed IDN names at composition")]
    [InlineData("api\0.example")]
    [InlineData("a..example")]
    public void IssueCertificate_InvalidAlternativeName_ShouldRejectAtComposition(string alternativeName)
    {
        // Arrange
        ISecretStoreResourceDescriptor descriptor = CreateDescriptor();

        // Act
        Action declare = () => descriptor.IssueCertificate("api", "CN=api", [alternativeName]);

        // Assert
        Should.Throw<ArgumentException>(declare);
    }

    private static ISecretStoreResourceDescriptor CreateDescriptor() =>
        Application.CreateBuilder((ApplicationName)"appa", []).AddSecretStore(SecretStoreManifestFactory.Create());
}
