using System;
using System.Buffers.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A client secret is never kept: the credential holds a verifier, and verifying is all it does.
/// </summary>
public class IdentityClientSecretCredentialTests
{
    private static readonly DateTimeOffset _now = TestClients.Now;

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Generate: Should hand back 256 random bits once and verify them")]
    public void Generate_ShouldReturnASecretTheCredentialVerifies()
    {
        // Act
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Generate("primary", out string secret, "Primary");

        // Assert
        secret.Length.ShouldBe(43);
        Base64Url.DecodeFromChars(secret).Length.ShouldBe(32);
        credential.Id.Value.ShouldBe("primary");
        credential.DisplayName.ShouldBe("Primary");
        credential.Kind.ShouldBe(IdentityCredentialKind.Key);
        credential.Iterations.ShouldBe(1);
        credential.Verify(secret, _now).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Generate: Should make a different secret and salt each time")]
    public void Generate_Twice_ShouldNotRepeatTheSecretOrTheSalt()
    {
        // Act
        IdentityClientSecretCredential first = IdentityClientSecretCredential.Generate("primary", out string firstSecret);
        IdentityClientSecretCredential second = IdentityClientSecretCredential.Generate("primary", out string secondSecret);

        // Assert
        firstSecret.ShouldNotBe(secondSecret);
        first.Salt.ToArray().ShouldNotBe(second.Salt.ToArray());
        first.Verify(secondSecret, _now).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should derive a caller's secret slowly and verify it")]
    public void Create_WithAChosenSecret_ShouldDeriveSlowlyAndVerify()
    {
        // Act
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Create("legacy", "correct horse battery staple");

        // Assert
        credential.Iterations.ShouldBe(IdentityClientSecretCredential.DerivationIterations);
        credential.Iterations.ShouldBe(600_000);
        credential.Verifier.Length.ShouldBe(32);
        credential.Salt.Length.ShouldBe(16);
        credential.Verify("correct horse battery staple", _now).ShouldBeTrue();
        credential.Verify("correct horse battery stapl", _now).ShouldBeFalse();
        credential.Verify("Correct horse battery staple", _now).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a secret outside printable ASCII, a blank one, and one too long")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pass\nword")]
    [InlineData("pässword")]
    [InlineData("pass\u007Fword")]
    public void Create_WithASecretOutsideTheGrammar_ShouldThrowWithoutEchoingIt(string secret)
    {
        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(
            () => IdentityClientSecretCredential.Create("legacy", secret));

        // Assert
        exception.ParamName.ShouldBe("secret");
        if (secret.Trim().Length > 0)
        {
            exception.Message.ShouldNotContain(secret, Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Create: Should refuse a null secret, a secret beyond the limit, and a default identifier")]
    public void Create_WithAMissingOrOversizedArgument_ShouldThrow()
    {
        // Act
        ArgumentNullException nullSecret = Should.Throw<ArgumentNullException>(
            () => IdentityClientSecretCredential.Create("legacy", null!));
        ArgumentException tooLong = Should.Throw<ArgumentException>(
            () => IdentityClientSecretCredential.Create("legacy", new string('s', IdentityClientSecretCredential.MaxSecretLength + 1)));
        ArgumentException noId = Should.Throw<ArgumentException>(
            () => IdentityClientSecretCredential.Generate(default, out _));

        // Assert
        IdentityClientSecretCredential.MaxSecretLength.ShouldBe(512);
        nullSecret.ParamName.ShouldBe("secret");
        tooLong.ParamName.ShouldBe("secret");
        noId.ParamName.ShouldBe("id");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Generate: Should refuse a window that ends before it starts and a display name outside its rule")]
    public void Generate_WithAnInvalidWindowOrDisplayName_ShouldThrowArgumentException()
    {
        // Act
        ArgumentException window = Should.Throw<ArgumentException>(
            () => IdentityClientSecretCredential.Generate("primary", out _, notBefore: _now, expiresAt: _now));
        ArgumentException displayName = Should.Throw<ArgumentException>(
            () => IdentityClientSecretCredential.Generate("primary", out _, displayName: "line\nbreak"));

        // Assert
        window.ParamName.ShouldBe("expiresAt");
        displayName.ParamName.ShouldBe("displayName");
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Verify: Should accept the secret only inside its validity window")]
    public void Verify_OutsideTheValidityWindow_ShouldReturnFalse()
    {
        // Arrange
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Generate(
            "primary",
            out string secret,
            notBefore: _now,
            expiresAt: _now.AddDays(30));

        // Act
        bool early = credential.Verify(secret, _now.AddTicks(-1));
        bool atStart = credential.Verify(secret, _now);
        bool lastInstant = credential.Verify(secret, _now.AddDays(30).AddTicks(-1));
        bool atExpiry = credential.Verify(secret, _now.AddDays(30));

        // Assert
        early.ShouldBeFalse();
        atStart.ShouldBeTrue();
        lastInstant.ShouldBeTrue();
        atExpiry.ShouldBeFalse();
        credential.IsUsable(_now).ShouldBeTrue();
        credential.IsUsable(_now.AddDays(30)).ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [IdentityHub] - Verify: Should refuse a null secret and one outside the grammar")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pass\nword")]
    public void Verify_WithASecretOutsideTheGrammar_ShouldReturnFalse(string? presented)
    {
        // Arrange
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Generate("primary", out _);

        // Act
        bool verified = credential.Verify(presented, _now);

        // Assert
        verified.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Verify: Should refuse a secret longer than the limit without deriving anything")]
    public void Verify_WithASecretBeyondTheLimit_ShouldReturnFalse()
    {
        // Arrange
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Generate("primary", out _);

        // Act
        bool verified = credential.Verify(new string('s', IdentityClientSecretCredential.MaxSecretLength + 1), _now);

        // Assert
        verified.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - ToString: Should name the credential and never its material")]
    public void ToString_ShouldNotRevealTheSecretOrTheVerifier()
    {
        // Arrange
        IdentityClientSecretCredential credential = IdentityClientSecretCredential.Generate("primary", out string secret);

        // Act
        string text = credential.ToString();

        // Assert
        text.ShouldBe("primary (Key)");
        text.ShouldNotContain(secret, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Restore: Should rebuild a credential that verifies the same secret")]
    public void Restore_WithAStoredVerifier_ShouldVerifyTheSameSecret()
    {
        // Arrange
        IdentityClientSecretCredential original = IdentityClientSecretCredential.Generate(
            "primary",
            out string secret,
            "Primary",
            _now,
            _now.AddDays(30));

        // Act
        IdentityClientSecretCredential restored = IdentityClientSecretCredential.Restore(
            original.Id,
            original.DisplayName,
            original.NotBefore,
            original.ExpiresAt,
            original.Salt.ToArray(),
            original.Verifier.ToArray(),
            original.Iterations);

        // Assert
        restored.Verify(secret, _now).ShouldBeTrue();
        restored.Verify(secret + "x", _now).ShouldBeFalse();
        restored.DisplayName.ShouldBe("Primary");
        restored.NotBefore.ShouldBe(_now);
        restored.ExpiresAt.ShouldBe(_now.AddDays(30));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Restore: Should refuse a verifier this type does not derive")]
    public void Restore_WithADamagedVerifier_ShouldThrowFormatException()
    {
        // Arrange
        byte[] salt = new byte[16];
        byte[] verifier = new byte[32];

        // Act + Assert
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, null, verifier, 1));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, new byte[15], verifier, 1));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, new byte[65], verifier, 1));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, salt, null, 1));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, salt, new byte[31], 1));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, salt, verifier, 0));
        Should.Throw<FormatException>(() => IdentityClientSecretCredential.Restore("primary", null, null, null, salt, verifier, 10_000_001));
    }

    [Fact(DisplayName = "Cohesion Test [IdentityHub] - Restore: Should copy the stored bytes")]
    public void Restore_ThenChangingTheSource_ShouldNotChangeTheCredential()
    {
        // Arrange
        IdentityClientSecretCredential original = IdentityClientSecretCredential.Generate("primary", out string secret);
        byte[] salt = original.Salt.ToArray();
        byte[] verifier = original.Verifier.ToArray();
        IdentityClientSecretCredential restored = IdentityClientSecretCredential.Restore(
            original.Id, null, null, null, salt, verifier, original.Iterations);

        // Act
        Array.Clear(salt);
        Array.Clear(verifier);

        // Assert
        restored.Verify(secret, _now).ShouldBeTrue();
    }
}
