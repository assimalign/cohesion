using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Verifies ES256 compact-token writing and its integration with parsing, signature
/// verification, and the existing token validation path.
/// </summary>
public sealed class JsonWebTokenWriterTests
{
    private const string issuer = "cohesion-gateway";
    private const string audience = "secret-store";
    private const string keyId = "gateway-key-1";
    private static readonly DateTimeOffset _now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Write: ES256 token round-trips through parse and verification")]
    public void Write_WhenEs256DescriptorProvided_ShouldRoundTripAndVerify()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa publicKey = CreatePublicKey(privateKey);
        IJsonWebTokenWriter writer = JsonWebTokenWriter.CreateEs256(privateKey, keyId);
        JsonWebTokenDescriptor descriptor = CreateDescriptor(_now.AddHours(1));
        descriptor.Claims.Add(new IdentityClaim("scope", "bootstrap"));

        // Act
        string compact = writer.Write(descriptor);
        JsonWebToken token = JsonWebToken.Parse(compact);
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(publicKey, keyId);

        // Assert
        token.Algorithm.ShouldBe(JoseAlgorithms.ES256);
        token.Header.KeyId.ShouldBe(keyId);
        token.Header.Type.ShouldBe("JWT");
        token.Issuer.ShouldBe(issuer);
        token.Audiences.ShouldBe(new[] { audience });
        token.ExpiresAt.ShouldBe(_now.AddHours(1));
        token.Id.ShouldBe("bootstrap-1");
        token.Claims.TryGet("scope", out IIdentityClaim? scope).ShouldBeTrue();
        scope!.Value.AsString().ShouldBe("bootstrap");
        Base64Url.DecodeFromChars(token.Parts!.Signature).Length.ShouldBe(64);
        Verify(verifier, token).ShouldBeTrue();
        descriptor.Header.Parameters.ShouldBeEmpty();
        descriptor.Parts.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Verify: A tampered ES256 payload is rejected")]
    public void Verify_WhenPayloadTampered_ShouldReturnFalse()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa publicKey = CreatePublicKey(privateKey);
        string compact = JsonWebTokenWriter.CreateEs256(privateKey, keyId)
            .Write(CreateDescriptor(_now.AddHours(1)));
        JsonWebTokenParts original = JsonWebToken.Parse(compact).Parts!;
        string tamperedPayload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            "{\"iss\":\"cohesion-gateway\",\"aud\":\"attacker\",\"exp\":1788699600}"));
        JsonWebToken tampered = JsonWebToken.Parse(
            string.Concat(original.Header, ".", tamperedPayload, ".", original.Signature));
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(publicKey, keyId);

        // Act
        bool verified = Verify(verifier, tampered);

        // Assert
        verified.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Verify: An ES256 token is rejected by the wrong key")]
    public void Verify_WhenEcdsaKeyIsWrong_ShouldReturnFalse()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JsonWebToken token = JsonWebToken.Parse(
            JsonWebTokenWriter.CreateEs256(privateKey, keyId).Write(CreateDescriptor(_now.AddHours(1))));
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(wrongKey, keyId);

        // Act
        bool verified = Verify(verifier, token);

        // Assert
        verified.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Validate: Written exp and aud use the existing validation path")]
    public void Validate_WhenWrittenTokenIsExpiredAndAudienceDiffers_ShouldReportExistingCodes()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JsonWebToken token = JsonWebToken.Parse(
            JsonWebTokenWriter.CreateEs256(privateKey, keyId).Write(CreateDescriptor(_now.AddHours(1))));
        JsonWebTokenValidationOptions options = new(_now.AddHours(2))
        {
            ClockSkew = TimeSpan.Zero,
            ExpectedAudience = "configuration-store",
            ExpectedIssuer = issuer,
        };
        options.AllowedAlgorithms.Add(JoseAlgorithms.ES256);

        // Act
        TokenValidationResult result = token.Validate(options);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.Expired);
        result.Errors.ShouldContain(error => error.Code == TokenValidationCodes.AudienceMismatch);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Write: A registered claim with two descriptor sources is rejected")]
    public void Write_WhenRegisteredClaimHasTwoSources_ShouldThrow()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IJsonWebTokenWriter writer = JsonWebTokenWriter.CreateEs256(privateKey, keyId);
        JsonWebTokenDescriptor descriptor = CreateDescriptor(_now.AddHours(1));
        descriptor.Claims.Add(new IdentityClaim(IdentityClaimTypes.Issuer, issuer));

        // Act
        Action write = () => writer.Write(descriptor);

        // Assert
        Should.Throw<ArgumentException>(write);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - CreateEs256: A non-P256 key is rejected")]
    public void CreateEs256_WhenKeyIsNotP256_ShouldThrow()
    {
        // Arrange
        using ECDsa privateKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        // Act
        Action create = () => JsonWebTokenWriter.CreateEs256(privateKey, keyId);

        // Assert
        Should.Throw<ArgumentException>(create);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - CreateEs256: A same-size non-NIST curve is rejected")]
    public void CreateEs256_WhenCurveIsSecp256K1_ShouldThrow()
    {
        // Arrange
        ECDsa privateKey;
        try
        {
            privateKey = ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1"));
        }
        catch (PlatformNotSupportedException)
        {
            // macOS (Apple CryptoKit) cannot generate secp256k1 keys, so the rejection path cannot be
            // reached there; Windows and Linux legs exercise it. xUnit v2 has no dynamic skip.
            return;
        }
        using ECDsa _ = privateKey;

        // Act
        Action create = () => JsonWebTokenWriter.CreateEs256(privateKey, keyId);

        // Assert
        Should.Throw<ArgumentException>(create);
    }

    private static JsonWebTokenDescriptor CreateDescriptor(DateTimeOffset expiresAt)
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = "bootstrap-1",
            Issuer = issuer,
            Subject = new SubjectIdentifier("gateway"),
            TokenType = "JWT",
            IssuedAt = _now,
            ExpiresAt = expiresAt,
        };
        descriptor.Audiences.Add(audience);
        return descriptor;
    }

    private static ECDsa CreatePublicKey(ECDsa privateKey)
    {
        byte[] subjectPublicKeyInfo = privateKey.ExportSubjectPublicKeyInfo();
        ECDsa publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int bytesRead);
        bytesRead.ShouldBe(subjectPublicKeyInfo.Length);
        return publicKey;
    }

    private static bool Verify(IJsonWebTokenSignatureVerifier verifier, JsonWebToken token)
    {
        string algorithm = token.Algorithm!;
        byte[] signingInput = Encoding.ASCII.GetBytes(token.SigningInput!);
        byte[] signature = Base64Url.DecodeFromChars(token.Parts!.Signature);
        return verifier.CanVerify(algorithm, token.Header.KeyId) &&
            verifier.Verify(algorithm, signingInput, signature);
    }
}
