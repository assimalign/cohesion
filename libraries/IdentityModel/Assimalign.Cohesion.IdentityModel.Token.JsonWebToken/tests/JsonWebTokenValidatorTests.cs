using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Verifies the ES256 validator's accept/reject matrix: issuer trust, key selection, signature, algorithm,
/// required claims, temporal and lifetime rules, subject rules, and the caller-applied audience rule.
/// </summary>
public sealed class JsonWebTokenValidatorTests : IDisposable
{
    private const string displayPrefix = "Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebTokenValidator: ";
    private const string issuer = "application";
    private const string subject = "gateway";
    private const string audience = "secret-store";
    private static readonly DateTimeOffset _now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _maximumLifetime = TimeSpan.FromHours(24);

    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebKey _publicKey;

    public JsonWebTokenValidatorTests()
    {
        using JsonDocument document = JsonDocument.Parse(
            JsonWebKeyTests.CreateJwk(_signingKey.ExportParameters(includePrivateParameters: false)));
        JsonWebKey.TryParse(document.RootElement, out JsonWebKey? key).ShouldBeTrue();
        _publicKey = key!;
    }

    public void Dispose() => _signingKey.Dispose();

    [Fact(DisplayName = displayPrefix + "TryValidate: A trusted, well-formed ES256 token is accepted")]
    public void TryValidate_WhenTokenSatisfiesProfile_ReturnsTokenAndAudienceMatches()
    {
        // Arrange
        IJsonWebTokenValidator validator = CreateValidator();
        string compact = Write(Descriptor());

        // Act
        bool valid = validator.TryValidate(compact, _now, out JsonWebToken? token);

        // Assert
        valid.ShouldBeTrue();
        token.ShouldNotBeNull().Issuer.ShouldBe(issuer);
        JsonWebTokenValidator.HasAudience(token, audience).ShouldBeTrue();
        JsonWebTokenValidator.HasAudience(token, "other").ShouldBeFalse();
        JsonWebTokenValidator.HasAudience(token, null).ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "TryValidate: A lifetime exactly at the ceiling is accepted")]
    public void TryValidate_WhenLifetimeEqualsMaximum_ReturnsTrue()
    {
        // Arrange
        IJsonWebTokenValidator validator = CreateValidator();
        JsonWebTokenDescriptor descriptor = Descriptor();
        descriptor.ExpiresAt = _now + _maximumLifetime;

        // Act
        bool valid = validator.TryValidate(Write(descriptor), _now, out _);

        // Assert
        valid.ShouldBeTrue();
    }

    [Theory(DisplayName = displayPrefix + "TryValidate: Every violated rule rejects the token")]
    [InlineData("untrusted-issuer")]
    [InlineData("unknown-kid")]
    [InlineData("wrong-key")]
    [InlineData("tampered")]
    [InlineData("expired")]
    [InlineData("over-lifetime")]
    [InlineData("future-issued")]
    [InlineData("not-yet-valid")]
    [InlineData("expires-before-issued")]
    [InlineData("missing-jti")]
    [InlineData("missing-nbf")]
    [InlineData("missing-aud")]
    [InlineData("unsecured")]
    [InlineData("relabelled-algorithm")]
    [InlineData("malformed")]
    public void TryValidate_WhenRuleIsViolated_ReturnsFalse(string violation)
    {
        // Arrange
        IJsonWebTokenValidator validator = CreateValidator();
        JsonWebTokenDescriptor descriptor = Descriptor();
        string compact;
        switch (violation)
        {
            case "untrusted-issuer":
                descriptor.Issuer = "someone-else";
                compact = Write(descriptor);
                break;
            case "unknown-kid":
                compact = JsonWebTokenWriter.CreateEs256(_signingKey, "unknown").Write(descriptor);
                break;
            case "wrong-key":
                using (ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                {
                    compact = JsonWebTokenWriter.CreateEs256(other, _publicKey.KeyId!).Write(descriptor);
                }
                break;
            case "tampered":
                string[] segments = Write(descriptor).Split('.');
                string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(segments[1]))
                    .Replace(audience, "attacker", StringComparison.Ordinal);
                compact = string.Join('.', segments[0], Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)), segments[2]);
                break;
            case "expired":
                descriptor.IssuedAt = _now.AddHours(-2);
                descriptor.NotBefore = _now.AddHours(-2);
                descriptor.ExpiresAt = _now.AddHours(-1);
                compact = Write(descriptor);
                break;
            case "over-lifetime":
                descriptor.ExpiresAt = _now + _maximumLifetime + TimeSpan.FromSeconds(1);
                compact = Write(descriptor);
                break;
            case "future-issued":
                descriptor.IssuedAt = _now.AddMinutes(6);
                compact = Write(descriptor);
                break;
            case "not-yet-valid":
                descriptor.NotBefore = _now.AddMinutes(6);
                compact = Write(descriptor);
                break;
            case "expires-before-issued":
                descriptor.IssuedAt = _now.AddMinutes(4);
                descriptor.ExpiresAt = _now.AddMinutes(3);
                compact = Write(descriptor);
                break;
            case "missing-jti":
                descriptor.Id = null;
                compact = Write(descriptor);
                break;
            case "missing-nbf":
                descriptor.NotBefore = null;
                compact = Write(descriptor);
                break;
            case "missing-aud":
                descriptor.Audiences.Clear();
                compact = Write(descriptor);
                break;
            case "unsecured":
                compact = Encode("{\"alg\":\"none\",\"kid\":\"" + _publicKey.KeyId + "\"}") + "." +
                    Write(descriptor).Split('.')[1] + ".";
                break;
            case "relabelled-algorithm":
                string[] parts = Write(descriptor).Split('.');
                compact = Encode("{\"alg\":\"ES384\",\"kid\":\"" + _publicKey.KeyId + "\",\"typ\":\"JWT\"}") + "." +
                    parts[1] + "." + parts[2];
                break;
            default:
                compact = "not-a-token";
                break;
        }

        // Act
        bool valid = validator.TryValidate(compact, _now, out JsonWebToken? token);

        // Assert
        valid.ShouldBeFalse();
        token.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "TryValidate: An expected subject is enforced exactly")]
    public void TryValidate_WhenExpectedSubjectDiffers_ReturnsFalse()
    {
        // Arrange
        IJsonWebTokenValidator matching = CreateValidator(expectedSubject: subject);
        IJsonWebTokenValidator mismatching = CreateValidator(expectedSubject: "Gateway");
        string compact = Write(Descriptor());

        // Act
        bool accepted = matching.TryValidate(compact, _now, out _);
        bool rejected = mismatching.TryValidate(compact, _now, out _);

        // Assert
        accepted.ShouldBeTrue();
        rejected.ShouldBeFalse();
    }

    [Fact(DisplayName = displayPrefix + "TryValidate: A correctly signed token without a kid header selects no key")]
    public void TryValidate_WhenHeaderOmitsKeyId_ReturnsFalse()
    {
        // Arrange
        IJsonWebTokenValidator validator = CreateValidator();
        string payload = Write(Descriptor()).Split('.')[1];
        string withKeyId = Sign("{\"alg\":\"ES256\",\"kid\":\"" + _publicKey.KeyId + "\",\"typ\":\"JWT\"}", payload);
        string withoutKeyId = Sign("{\"alg\":\"ES256\",\"typ\":\"JWT\"}", payload);

        // Act
        bool accepted = validator.TryValidate(withKeyId, _now, out _);
        bool rejected = validator.TryValidate(withoutKeyId, _now, out JsonWebToken? token);

        // Assert
        accepted.ShouldBeTrue();
        rejected.ShouldBeFalse();
        token.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "TryValidate: A non-string sub is refused unless the caller applies its own subject rule")]
    public void TryValidate_WhenSubjectIsNotAString_AppliesRequireSubject()
    {
        // Arrange
        IJsonWebTokenValidator requiring = CreateValidator();
        IJsonWebTokenValidator relaxed = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            issuer,
            new JsonWebKeySet(_publicKey),
            _maximumLifetime)
        {
            RequireSubject = false,
        });
        long issuedAt = _now.ToUnixTimeSeconds();
        string payload = Encode(
            "{\"iss\":\"" + issuer + "\",\"sub\":5,\"aud\":\"" + audience + "\",\"iat\":" + issuedAt +
            ",\"nbf\":" + issuedAt + ",\"exp\":" + (issuedAt + 3600) + ",\"jti\":\"" + Guid.NewGuid().ToString("N") + "\"}");
        string compact = Sign("{\"alg\":\"ES256\",\"kid\":\"" + _publicKey.KeyId + "\",\"typ\":\"JWT\"}", payload);

        // Act
        bool required = requiring.TryValidate(compact, _now, out _);
        bool relaxedResult = relaxed.TryValidate(compact, _now, out JsonWebToken? token);

        // Assert
        required.ShouldBeFalse();
        relaxedResult.ShouldBeTrue();
        token.ShouldNotBeNull().Subject.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "TryValidate: The issuer resolver observes keys added after construction")]
    public void TryValidate_WhenResolverGainsIssuer_AcceptsAfterTrustIsGranted()
    {
        // Arrange
        var trusted = new Dictionary<string, JsonWebKeySet>(StringComparer.Ordinal);
        IJsonWebTokenValidator validator = JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            name => trusted.TryGetValue(name, out JsonWebKeySet? keys) ? keys : null,
            _maximumLifetime));
        string compact = Write(Descriptor());
        bool before = validator.TryValidate(compact, _now, out _);

        // Act
        trusted[issuer] = new JsonWebKeySet(_publicKey);
        bool after = validator.TryValidate(compact, _now, out _);

        // Assert
        before.ShouldBeFalse();
        after.ShouldBeTrue();
    }

    [Fact(DisplayName = displayPrefix + "Profile: Invalid lifetime and skew are refused")]
    public void Constructor_WhenLifetimeOrSkewIsInvalid_Throws()
    {
        // Arrange
        var keys = new JsonWebKeySet(_publicKey);

        // Act
        Action zeroLifetime = () => _ = new JsonWebTokenValidationProfile(issuer, keys, TimeSpan.Zero);
        Action negativeSkew = () => _ = new JsonWebTokenValidationProfile(issuer, keys, _maximumLifetime)
        {
            ClockSkew = TimeSpan.FromSeconds(-1),
        };
        Action blankIssuer = () => _ = new JsonWebTokenValidationProfile(" ", keys, _maximumLifetime);

        // Assert
        Should.Throw<ArgumentOutOfRangeException>(zeroLifetime);
        Should.Throw<ArgumentOutOfRangeException>(negativeSkew);
        Should.Throw<ArgumentException>(blankIssuer);
    }

    private IJsonWebTokenValidator CreateValidator(string? expectedSubject = null) =>
        JsonWebTokenValidator.CreateEs256(new JsonWebTokenValidationProfile(
            issuer,
            new JsonWebKeySet(_publicKey),
            _maximumLifetime)
        {
            ExpectedSubject = expectedSubject,
        });

    private string Write(JsonWebTokenDescriptor descriptor) =>
        JsonWebTokenWriter.CreateEs256(_signingKey, _publicKey.KeyId!).Write(descriptor);

    private static JsonWebTokenDescriptor Descriptor()
    {
        var descriptor = new JsonWebTokenDescriptor
        {
            Id = Guid.NewGuid().ToString("N"),
            Issuer = issuer,
            Subject = new SubjectIdentifier(subject),
            TokenType = "JWT",
            IssuedAt = _now,
            NotBefore = _now,
            ExpiresAt = _now.AddHours(1),
        };
        descriptor.Audiences.Add(audience);
        return descriptor;
    }

    private string Sign(string headerJson, string encodedPayload)
    {
        string signingInput = Encode(headerJson) + "." + encodedPayload;
        byte[] signature = _signingKey.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
