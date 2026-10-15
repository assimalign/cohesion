using System;
using System.Security.Cryptography.X509Certificates;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel.Tests;

/// <summary>Tests generated command values and default boundary guards.</summary>
public sealed class CommandValueObjectTests
{
    /// <summary>The parameter branch is case-sensitive and resource names have ASCII grammar.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CommandSource: shares delivery source grammar")]
    [InlineData("parameter:credential", true)]
    [InlineData("parameter:name:part", true)]
    [InlineData("Parameter:name:part", false)]
    [InlineData("Parameter:name", true)]
    [InlineData("source-name_1.2:key", true)]
    [InlineData("store:key:extra", false)]
    [InlineData("parameter: ", false)]
    [InlineData("store: ", false)]
    [InlineData("storé:key", false)]
    [InlineData("bad store:key", false)]
    [InlineData("literal:value", false)]
    [InlineData("LITERAL:value", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryCreate_CommandSource_ShouldMatchDeliveryGrammar(string? value, bool expected)
    {
        // Act
        bool valid = SecretStoreCommandSource.TryCreate(value, out SecretStoreCommandSource result);

        // Assert
        valid.ShouldBe(expected);
        if (expected) { result.Value.ShouldBe(value); }
        else { result.ShouldBe(default); }
    }

    /// <summary>Reserved storage spellings stay ordinal, and paths remain logical identifiers.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CommandPath: preserves ordinal reserved paths")]
    [InlineData("orders/key", true)]
    [InlineData("Trusted-issuers.json", true)]
    [InlineData("Certs/api", true)]
    [InlineData("certs", true)]
    [InlineData("trusted-issuers.json/child", true)]
    [InlineData("trusted-issuers.json", false)]
    [InlineData("certs/api", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void TryCreate_CommandPath_ShouldMatchReservedNamespaces(string? value, bool expected)
    {
        // Act
        bool valid = SecretStoreCommandPath.TryCreate(value, out _);

        // Assert
        valid.ShouldBe(expected);
    }

    /// <summary>Storage leaf names are not constrained to DNS hostnames.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CertificateName: validates a single storage segment")]
    [InlineData("API internal", true)]
    [InlineData("api.example", true)]
    [InlineData("...", true)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("api/leaf", false)]
    [InlineData("api\\leaf", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    public void TryCreate_CertificateName_ShouldMatchStorageGrammar(string? value, bool expected)
    {
        // Act
        bool valid = SecretStoreCertificateName.TryCreate(value, out _);

        // Assert
        valid.ShouldBe(expected);
    }

    /// <summary>Certificate DNS names retain the BCL's wildcard and internationalized-name support.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AlternativeName: matches BCL certificate DNS acceptance")]
    [InlineData("api.example")]
    [InlineData("*.example")]
    [InlineData("bücher.example")]
    [InlineData("api\0.example")]
    [InlineData("a..example")]
    [InlineData("\ud800.example")]
    public void TryCreate_DnsAlternativeName_ShouldMatchCertificateEncoder(string value)
    {
        // Arrange: the BCL encoder is the interoperability oracle, never the production validator.
        bool acceptedByEncoder;
        try
        {
            var builder = new SubjectAlternativeNameBuilder();
            builder.AddDnsName(value);
            acceptedByEncoder = true;
        }
        catch (ArgumentException)
        {
            acceptedByEncoder = false;
        }

        // Act
        bool valid = SecretStoreSubjectAlternativeName.TryCreate(value, out _);

        // Assert
        valid.ShouldBe(acceptedByEncoder);
    }

    /// <summary>IP addresses and case-insensitive DNS identity are supported.</summary>
    [Theory(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - AlternativeName: accepts IP addresses")]
    [InlineData("127.0.0.1")]
    [InlineData("2001:db8::1")]
    public void TryCreate_IpAlternativeName_ShouldAccept(string value)
    {
        // Act
        bool valid = SecretStoreSubjectAlternativeName.TryCreate(value, out SecretStoreSubjectAlternativeName result);

        // Assert
        valid.ShouldBeTrue();
        result.Value.ShouldBe(value);
    }

    /// <summary>DNS equality ignores case while storage names and sources preserve it.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - CommandValues: use each grammar's case rule")]
    public void Equals_DifferentCase_ShouldFollowEachGrammar()
    {
        // Arrange
        SecretStoreSubjectAlternativeName lower = "api.example";
        SecretStoreSubjectAlternativeName upper = "API.EXAMPLE";

        // Assert
        lower.ShouldBe(upper);
        lower.GetHashCode().ShouldBe(upper.GetHashCode());
        ((SecretStoreCertificateName)"api").ShouldNotBe((SecretStoreCertificateName)"API");
        ((SecretStoreCommandSource)"store:key").ShouldNotBe((SecretStoreCommandSource)"Store:key");
        ((SecretStoreCommandPath)"orders/key").ShouldNotBe((SecretStoreCommandPath)"Orders/key");
    }

    /// <summary>Default structs cannot bypass required declaration arguments.</summary>
    [Fact(DisplayName = "Cohesion Test [SecretStore.ApplicationModel] - Commands: reject default value objects")]
    public void Declare_DefaultValueObjects_ShouldRejectAtBoundary()
    {
        // Arrange
        ISecretStoreResourceDescriptor descriptor =
            Application.CreateBuilder((ApplicationName)"appa", []).AddSecretStore(SecretStoreManifestFactory.Create());

        // Act and assert
        Should.Throw<ArgumentException>(() => descriptor.AddSecret(default, "parameter:credential")).ParamName.ShouldBe("path");
        Should.Throw<ArgumentException>(() => descriptor.AddSecret("orders/key", default)).ParamName.ShouldBe("source");
        Should.Throw<ArgumentException>(() => descriptor.IssueCertificate(default, "CN=api")).ParamName.ShouldBe("name");
        Should.Throw<ArgumentException>(() => descriptor.IssueCertificate("api", "CN=api", [default])).ParamName.ShouldBe("subjectAlternativeNames");
    }
}
