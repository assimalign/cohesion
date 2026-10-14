using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public sealed class ApplicationCredentialValueTests
{
    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential schemes accept HTTP token characters")]
    [InlineData("Bearer")]
    [InlineData("DPoP")]
    [InlineData("AZaz09!#$%&'*+-.^_`|~")]
    public void TryCreate_TokenScheme_ShouldPreserveSpelling(string text)
    {
        // Act
        bool created = ApplicationCredentialScheme.TryCreate(text, out ApplicationCredentialScheme scheme);

        // Assert
        created.ShouldBeTrue();
        scheme.Value.ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential schemes refuse HTTP separators and non-ASCII characters")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer:")]
    [InlineData("Bearer/")]
    [InlineData("Bearer=")]
    [InlineData("Bearer(1)")]
    [InlineData("Bearer\u007f")]
    [InlineData("Béarer")]
    public void TryCreate_InvalidScheme_ShouldReturnFalse(string? text)
    {
        // Act
        bool created = ApplicationCredentialScheme.TryCreate(text, out ApplicationCredentialScheme scheme);

        // Assert
        created.ShouldBeFalse();
        scheme.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Credential schemes compare without case and values compare ordinally")]
    public void Equals_CredentialComponents_ShouldFollowTheirCaseRules()
    {
        // Arrange
        ApplicationCredentialScheme upper = "Bearer";
        ApplicationCredentialScheme lower = "bearer";
        ApplicationCredentialValue first = "OpaqueSecret";
        ApplicationCredentialValue second = "opaquesecret";

        // Act & Assert
        upper.ShouldBe(lower);
        upper.GetHashCode().ShouldBe(lower.GetHashCode());
        upper.CompareTo(lower).ShouldBe(0);
        first.ShouldNotBe(second);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential values preserve opaque single-line contents")]
    [InlineData("not-a-jwt:opaque/credential=with spaces")]
    [InlineData("\t secret\tvalue ")]
    [InlineData("opaque-値")]
    public void TryCreate_OpaqueValue_ShouldPreserveContents(string text)
    {
        // Act
        bool created = ApplicationCredentialValue.TryCreate(text, out ApplicationCredentialValue value);

        // Assert
        created.ShouldBeTrue();
        value.Value.ShouldBe(text);
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Credential values refuse empty and multiline input without echoing it")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t ")]
    [InlineData("secret\rvalue")]
    [InlineData("secret\nvalue")]
    public void TryCreate_InvalidValue_ShouldReturnFalse(string? text)
    {
        // Act
        bool created = ApplicationCredentialValue.TryCreate(text, out ApplicationCredentialValue value);

        // Assert
        created.ShouldBeFalse();
        value.ShouldBe(default);
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Credential values redact direct and formatted string displays")]
    public void ToString_CredentialValue_ShouldRedactEveryStringDisplay()
    {
        // Arrange
        ApplicationCredentialValue value = "opaque-secret";
        object boxed = value;
        var credential = new ApplicationCredential("Bearer", value, DateTimeOffset.UnixEpoch);

        // Act
        string[] displays =
        [
            value.ToString(),
            boxed.ToString()!,
            $"{value}",
            string.Empty + value,
            value + string.Empty,
            credential.ToString(),
            default(ApplicationCredentialValue).ToString(),
        ];

        // Assert
        foreach (string display in displays)
        {
            display.ShouldContain("<redacted>", Case.Sensitive);
            display.ShouldNotContain("opaque-secret", Case.Sensitive);
        }

        value.Value.ShouldBe("opaque-secret");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Credentials refuse default components at construction and with assignment")]
    public void ConstructorAndWith_DefaultComponents_ShouldRejectMissingValues()
    {
        // Arrange
        var credential = new ApplicationCredential("Bearer", "opaque-secret", DateTimeOffset.UnixEpoch);

        // Act & Assert
        Should.Throw<ArgumentException>(() => new ApplicationCredential(default, "opaque-secret", DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentException>(() => new ApplicationCredential("Bearer", default, DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentException>(() => credential with { Scheme = default });
        Should.Throw<ArgumentException>(() => credential with { Value = default });
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Credentials preserve validated record copying and deconstruction")]
    public void With_ValidComponents_ShouldPreserveRecordBehavior()
    {
        // Arrange
        var original = new ApplicationCredential("Bearer", "first-secret", DateTimeOffset.UnixEpoch);

        // Act
        var changed = original with { Scheme = "DPoP", Value = "second-secret", ExpiresAt = DateTimeOffset.UnixEpoch.AddHours(1) };
        (ApplicationCredentialScheme scheme, ApplicationCredentialValue value, DateTimeOffset expiresAt) = changed;

        // Assert
        scheme.Value.ShouldBe("DPoP");
        value.Value.ShouldBe("second-secret");
        expiresAt.ShouldBe(DateTimeOffset.UnixEpoch.AddHours(1));
        original.Value.Value.ShouldBe("first-secret");
        (original with { Scheme = "bearer" }).ShouldBe(original);
    }
}
