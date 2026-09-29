using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Verifies JSON Web Key parsing, RFC 7638 thumbprints, ECDSA key creation, the strict ES256 signing-key
/// profile, and key-set lookup.
/// </summary>
public sealed class JsonWebKeyTests
{
    private const string displayPrefix = "Cohesion Test [IdentityModel.Token.JsonWebToken] - JsonWebKey: ";

    // RFC 7638 §3.1 example key and its specified SHA-256 thumbprint.
    private const string rfc7638Key =
        "{\"kty\":\"RSA\",\"n\":\"0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw\",\"e\":\"AQAB\",\"alg\":\"RS256\",\"kid\":\"2011-04-29\"}";
    private const string rfc7638Thumbprint = "NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs";

    // RFC 7517 Appendix A.1 example EC P-256 public key.
    private const string rfc7517X = "MKBCTNIcKUSDii11ySs3526iDZ8AiTo7Tu6KPAqv7D4";
    private const string rfc7517Y = "4Etl6SRW2YiLUrN5vfvVHuhp7x8PxltmWWlbbM4IFyM";

    [Fact(DisplayName = displayPrefix + "ComputeThumbprint: The RFC 7638 example key yields its specified thumbprint")]
    public void ComputeThumbprint_WhenKeyIsRfc7638Example_ReturnsSpecifiedThumbprint()
    {
        // Arrange
        JsonWebKey.TryParse(Encoding.UTF8.GetBytes(rfc7638Key), out JsonWebKey? key).ShouldBeTrue();

        // Act
        string thumbprint = key.ComputeThumbprint();

        // Assert
        thumbprint.ShouldBe(rfc7638Thumbprint);
        key.KeyType.ShouldBe("RSA");
        key.Exponent.ShouldBe("AQAB");
        key.KeyId.ShouldBe("2011-04-29");
        key.Algorithm.ShouldBe("RS256");
    }

    [Fact(DisplayName = displayPrefix + "ComputeThumbprint: An EC key hashes only crv, kty, x, and y in lexicographic order")]
    public void ComputeThumbprint_WhenEcKeyHasOptionalMembersInAnyOrder_HashesCanonicalRequiredMembers()
    {
        // Arrange
        string json = "{\"use\":\"sig\",\"y\":\"" + rfc7517Y + "\",\"kid\":\"any\",\"x\":\"" + rfc7517X +
            "\",\"alg\":\"ES256\",\"crv\":\"P-256\",\"kty\":\"EC\"}";
        string canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + rfc7517X + "\",\"y\":\"" + rfc7517Y + "\"}";
        string expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        JsonWebKey.TryParse(Encoding.UTF8.GetBytes(json), out JsonWebKey? key).ShouldBeTrue();

        // Act
        string thumbprint = key.ComputeThumbprint();

        // Assert
        thumbprint.ShouldBe(expected);
    }

    [Fact(DisplayName = displayPrefix + "ComputeThumbprint: An unsupported key type is refused")]
    public void ComputeThumbprint_WhenKeyTypeIsUnsupported_Throws()
    {
        // Arrange
        JsonWebKey.TryParse("{\"kty\":\"oct\",\"k\":\"AQAB\"}"u8, out JsonWebKey? key).ShouldBeTrue();

        // Act
        Action compute = () => key.ComputeThumbprint();

        // Assert
        Should.Throw<NotSupportedException>(compute);
    }

    [Theory(DisplayName = displayPrefix + "TryParse: Malformed documents are rejected")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"EC\"")]
    [InlineData("{\"kty\":\"EC\"")]
    [InlineData("{\"kty\":\"EC\"} {}")]
    public void TryParse_WhenDocumentIsNotOneJsonObject_ReturnsFalse(string json)
    {
        // Arrange
        byte[] utf8 = Encoding.UTF8.GetBytes(json);

        // Act
        bool parsed = JsonWebKey.TryParse(utf8, out JsonWebKey? key);

        // Assert
        parsed.ShouldBeFalse();
        key.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "TryParse: A leading UTF-8 byte order mark is tolerated and non-string members are absent")]
    public void TryParse_WhenDocumentHasByteOrderMarkAndNumericMember_ParsesAndTreatsMemberAsAbsent()
    {
        // Arrange
        byte[] utf8 = [0xEF, 0xBB, 0xBF, .. "{\"kty\":\"EC\",\"crv\":5,\"d\":\"private\"}"u8];

        // Act
        bool parsed = JsonWebKey.TryParse(utf8, out JsonWebKey? key);

        // Assert
        parsed.ShouldBeTrue();
        key.ShouldNotBeNull().KeyType.ShouldBe("EC");
        key.Curve.ShouldBeNull();
    }

    [Fact(DisplayName = displayPrefix + "CreateECDsa: The public point round-trips from a generated key")]
    public void CreateECDsa_WhenKeyIsP256_ReturnsPublicKeyWithSamePoint()
    {
        // Arrange
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = generated.ExportParameters(includePrivateParameters: false);
        JsonWebKey key = ParseKey(CreateJwk(parameters));

        // Act
        using ECDsa created = key.CreateECDsa();

        // Assert
        ECParameters exported = created.ExportParameters(includePrivateParameters: false);
        exported.Q.X.ShouldBe(parameters.Q.X);
        exported.Q.Y.ShouldBe(parameters.Q.Y);
        exported.Curve.Oid.Value.ShouldBe(ECCurve.NamedCurves.nistP256.Oid.Value);
    }

    [Fact(DisplayName = displayPrefix + "CreateECDsa: A non-EC key is refused")]
    public void CreateECDsa_WhenKeyIsRsa_ThrowsInvalidOperation()
    {
        // Arrange
        JsonWebKey key = ParseKey(rfc7638Key);

        // Act
        Action create = () => key.CreateECDsa();

        // Assert
        Should.Throw<InvalidOperationException>(create);
    }

    [Fact(DisplayName = displayPrefix + "CreateECDsa: An off-curve point is refused")]
    public void CreateECDsa_WhenPointIsNotOnCurve_Throws()
    {
        // Arrange
        string x = Base64Url.EncodeToString(new byte[32]);
        JsonWebKey key = ParseKey("{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"y\":\"" + x + "\"}");

        // Act
        Exception exception = Should.Throw<Exception>(() => key.CreateECDsa());

        // Assert
        // The provider decides the type: CNG reports unsupported parameters, OpenSSL a cryptographic error.
        (exception is CryptographicException or PlatformNotSupportedException).ShouldBeTrue();
    }

    [Fact(DisplayName = displayPrefix + "TryValidateEs256SigningKey: A thumbprint-identified P-256 signing key is accepted")]
    public void TryValidateEs256SigningKey_WhenKeyMatchesProfile_ReturnsTrue()
    {
        // Arrange
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JsonWebKey key = ParseKey(CreateJwk(generated.ExportParameters(includePrivateParameters: false)));

        // Act
        bool valid = key.TryValidateEs256SigningKey(out string? failure);

        // Assert
        valid.ShouldBeTrue();
        failure.ShouldBeNull();
    }

    [Theory(DisplayName = displayPrefix + "TryValidateEs256SigningKey: Each profile violation is rejected with its rule")]
    [InlineData("extra", "unsupported or duplicate member 'extra'")]
    [InlineData("duplicate", "unsupported or duplicate member 'kty'")]
    [InlineData("missing-use", "requires string member 'use'")]
    [InlineData("rs256", "must be an EC P-256 key for ES256 signatures")]
    [InlineData("short", "must have 32-byte coordinates")]
    [InlineData("off-curve", "is not a valid P-256 point")]
    [InlineData("kid", "must equal its RFC 7638 thumbprint")]
    public void TryValidateEs256SigningKey_WhenKeyViolatesProfile_ReturnsFailure(string violation, string expected)
    {
        // Arrange
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = generated.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X);
        string y = Base64Url.EncodeToString(parameters.Q.Y);
        string thumbprint = ParseKey(CreateJwk(parameters)).ComputeThumbprint();
        string zero = Base64Url.EncodeToString(new byte[32]);
        string json = violation switch
        {
            "extra" => Jwk(x, y, thumbprint, "ES256", "\"use\":\"sig\",\"extra\":\"1\""),
            "duplicate" => Jwk(x, y, thumbprint, "ES256", "\"use\":\"sig\",\"kty\":\"EC\""),
            "missing-use" => Jwk(x, y, thumbprint, "ES256", null),
            "rs256" => Jwk(x, y, thumbprint, "RS256", "\"use\":\"sig\""),
            "short" => Jwk(Base64Url.EncodeToString(new byte[31]), y, thumbprint, "ES256", "\"use\":\"sig\""),
            "off-curve" => Jwk(zero, zero, thumbprint, "ES256", "\"use\":\"sig\""),
            _ => Jwk(x, y, "not-the-thumbprint", "ES256", "\"use\":\"sig\""),
        };
        JsonWebKey key = ParseKey(json);

        // Act
        bool valid = key.TryValidateEs256SigningKey(out string? failure);

        // Assert
        valid.ShouldBeFalse();
        failure.ShouldNotBeNull().ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = displayPrefix + "TryValidateEcdsaVerificationKey: A usable key needs only kty, crv, kid, and a valid point")]
    public void TryValidateEcdsaVerificationKey_WhenKeyIsUsable_ReturnsTrue()
    {
        // Arrange
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = generated.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X);
        string y = Base64Url.EncodeToString(parameters.Q.Y);
        JsonWebKey lenient = ParseKey(
            "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"y\":\"" + y + "\",\"kid\":\"\",\"extra\":1}");

        // Act
        bool valid = lenient.TryValidateEcdsaVerificationKey("P-256", out string? failure);

        // Assert
        valid.ShouldBeTrue();
        failure.ShouldBeNull();
        lenient.TryValidateEs256SigningKey(out _).ShouldBeFalse();
    }

    [Theory(DisplayName = displayPrefix + "TryValidateEcdsaVerificationKey: Each violation is rejected with its rule")]
    [InlineData("rsa", "must be an EC P-256 key")]
    [InlineData("other-curve", "must be an EC P-256 key")]
    [InlineData("missing-curve", "must be an EC P-256 key")]
    [InlineData("missing-kid", "requires a kid")]
    [InlineData("numeric-kid", "requires a kid")]
    [InlineData("missing-y", "is not a valid EC P-256 public key")]
    [InlineData("bad-base64url", "is not a valid EC P-256 public key")]
    [InlineData("off-curve", "is not a valid EC P-256 public key")]
    public void TryValidateEcdsaVerificationKey_WhenKeyViolatesRule_ReturnsFailure(string violation, string expected)
    {
        // Arrange
        using ECDsa generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ECParameters parameters = generated.ExportParameters(includePrivateParameters: false);
        string x = Base64Url.EncodeToString(parameters.Q.X);
        string y = Base64Url.EncodeToString(parameters.Q.Y);
        string zero = Base64Url.EncodeToString(new byte[32]);
        string json = violation switch
        {
            "rsa" => rfc7638Key,
            "other-curve" => "{\"kty\":\"EC\",\"crv\":\"P-384\",\"x\":\"" + x + "\",\"y\":\"" + y + "\",\"kid\":\"k\"}",
            "missing-curve" => "{\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\",\"kid\":\"k\"}",
            "missing-kid" => "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}",
            "numeric-kid" => "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"y\":\"" + y + "\",\"kid\":1}",
            "missing-y" => "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"kid\":\"k\"}",
            "bad-base64url" => "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"***\",\"y\":\"" + y + "\",\"kid\":\"k\"}",
            _ => "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + zero + "\",\"y\":\"" + zero + "\",\"kid\":\"k\"}",
        };
        JsonWebKey key = ParseKey(json);

        // Act
        bool valid = key.TryValidateEcdsaVerificationKey("P-256", out string? failure);

        // Assert
        valid.ShouldBeFalse();
        failure.ShouldNotBeNull().ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = displayPrefix + "JsonWebKeySet.Find: Keys are selected by exact kid only")]
    public void Find_WhenKeyIdMatchesOrNot_ReturnsExactMatchOnly()
    {
        // Arrange
        JsonWebKey first = ParseKey("{\"kty\":\"EC\",\"kid\":\"one\"}");
        JsonWebKey second = ParseKey("{\"kty\":\"EC\",\"kid\":\"two\"}");
        JsonWebKey anonymous = ParseKey("{\"kty\":\"EC\"}");
        var set = new JsonWebKeySet(first, anonymous, second);

        // Act
        JsonWebKey? found = set.Find("two");

        // Assert
        found.ShouldBeSameAs(second);
        set.Find("TWO").ShouldBeNull();
        set.Find(null).ShouldBeNull();
        set.Keys.Count.ShouldBe(3);
    }

    [Fact(DisplayName = displayPrefix + "JsonWebKeySet: A null key is refused")]
    public void Constructor_WhenKeyIsNull_ThrowsArgument()
    {
        // Arrange
        JsonWebKey key = ParseKey("{\"kty\":\"EC\",\"kid\":\"one\"}");

        // Act
        Action create = () => _ = new JsonWebKeySet(key, null!);

        // Assert
        Should.Throw<ArgumentException>(create);
    }

    internal static string CreateJwk(ECParameters parameters)
    {
        string x = Base64Url.EncodeToString(parameters.Q.X);
        string y = Base64Url.EncodeToString(parameters.Q.Y);
        string canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        string kid = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return Jwk(x, y, kid, "ES256", "\"use\":\"sig\"");
    }

    private static string Jwk(string x, string y, string kid, string algorithm, string? tail) =>
        "{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"" + x + "\",\"y\":\"" + y + "\",\"kid\":\"" + kid +
        "\",\"alg\":\"" + algorithm + "\"" + (tail is null ? string.Empty : "," + tail) + "}";

    private static JsonWebKey ParseKey(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonWebKey.TryParse(document.RootElement, out JsonWebKey? key).ShouldBeTrue();
        return key;
    }
}
