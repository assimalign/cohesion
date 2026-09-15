using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Tests;

/// <summary>
/// Verifies the reusable RSA and ECDSA JSON Web Token signature-verification primitives.
/// </summary>
public sealed class JsonWebTokenSignatureVerifierTests
{
    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Verify: RS256 signature round-trips with a public key")]
    public void Verify_WhenRs256SignatureIsValid_ShouldReturnTrue()
    {
        // Arrange
        using RSA privateKey = RSA.Create(2048);
        using RSA publicKey = RSA.Create();
        publicKey.ImportParameters(privateKey.ExportParameters(includePrivateParameters: false));
        string header = Encode("{\"alg\":\"RS256\",\"kid\":\"rsa-key-1\"}");
        string payload = Encode("{\"iss\":\"issuer\",\"aud\":\"resource\"}");
        string signingInputText = string.Concat(header, ".", payload);
        byte[] signingInput = Encoding.ASCII.GetBytes(signingInputText);
        byte[] signature = privateKey.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        JsonWebToken token = JsonWebToken.Parse(
            string.Concat(signingInputText, ".", Base64Url.EncodeToString(signature)));
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateRsa(publicKey, "rsa-key-1");

        // Act
        bool verified = verifier.CanVerify(token.Algorithm!, token.Header.KeyId) &&
            verifier.Verify(token.Algorithm!, signingInput, signature);

        // Assert
        verified.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - CanVerify: ECDSA algorithm must match the named curve")]
    public void CanVerify_WhenEcdsaAlgorithmDoesNotMatchNamedCurve_ShouldReturnFalse()
    {
        // Arrange
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(p256);

        // Act
        bool canVerify = verifier.CanVerify(JoseAlgorithms.ES384, keyId: null);

        // Assert
        canVerify.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - CreateEcdsa: A non-JOSE curve is rejected")]
    public void CreateEcdsa_WhenCurveIsSecp256K1_ShouldThrow()
    {
        // Arrange
        ECDsa publicKey;
        try
        {
            publicKey = ECDsa.Create(ECCurve.CreateFromFriendlyName("secp256k1"));
        }
        catch (PlatformNotSupportedException)
        {
            // macOS (Apple CryptoKit) cannot generate secp256k1 keys, so the rejection path cannot be
            // reached there; Windows and Linux legs exercise it. xUnit v2 has no dynamic skip.
            return;
        }
        using ECDsa _ = publicKey;

        // Act
        Action create = () => JsonWebTokenSignatureVerifier.CreateEcdsa(publicKey);

        // Assert
        Should.Throw<ArgumentException>(create);
    }

    [Fact(DisplayName = "Cohesion Test [IdentityModel.Token.JsonWebToken] - Verify: A malformed ES256 signature length is rejected")]
    public void Verify_WhenEs256SignatureLengthIsMalformed_ShouldReturnFalse()
    {
        // Arrange
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(p256);
        byte[] signingInput = Encoding.ASCII.GetBytes("eyJhbGciOiJFUzI1NiJ9.e30");
        var malformedSignature = new byte[63];

        // Act
        bool verified = verifier.Verify(JoseAlgorithms.ES256, signingInput, malformedSignature);

        // Assert
        verified.ShouldBeFalse();
    }

    private static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
