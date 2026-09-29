using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

/// <summary>
/// Verifies ES256 compact JSON Web Tokens against a profile's trusted issuer keys and claim rules.
/// </summary>
internal sealed class Es256JsonWebTokenValidator : IJsonWebTokenValidator
{
    private static readonly string[] _requiredClaims = ["iss", "sub", "aud", "exp", "nbf", "iat", "jti"];

    public Es256JsonWebTokenValidator(JsonWebTokenValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
    }

    /// <inheritdoc />
    public JsonWebTokenValidationProfile Profile { get; }

    /// <inheritdoc />
    public bool TryValidate(string compactToken, DateTimeOffset now, [NotNullWhen(true)] out JsonWebToken? token)
    {
        token = null;
        if (!JsonWebToken.TryParse(compactToken, out JsonWebToken? candidate) ||
            candidate is null ||
            string.IsNullOrWhiteSpace(candidate.Issuer) ||
            string.IsNullOrWhiteSpace(candidate.Algorithm) ||
            candidate.SigningInput is null ||
            candidate.Parts is null)
        {
            return false;
        }

        if (Profile.ExpectedSubject is { } expectedSubject &&
            !string.Equals(candidate.Subject?.Value, expectedSubject, StringComparison.Ordinal))
        {
            return false;
        }

        JsonWebKey? key = Profile.IssuerKeys.Invoke(candidate.Issuer)?.Find(candidate.Header.KeyId);
        if (key is null || !VerifySignature(candidate, key))
        {
            return false;
        }

        var options = new JsonWebTokenValidationOptions(now)
        {
            ClockSkew = Profile.ClockSkew,
            ExpectedIssuer = candidate.Issuer,
            AllowUnsecured = false,
        };
        options.AllowedAlgorithms.Add(JoseAlgorithms.ES256);
        for (int index = 0; index < _requiredClaims.Length; index++)
        {
            options.RequiredClaims.Add(_requiredClaims[index]);
        }

        bool valid = candidate.Validate(options).Succeeded &&
            SatisfiesSubjectRule(candidate) &&
            !string.IsNullOrWhiteSpace(candidate.Id) &&
            candidate.IssuedAt is { } issuedAt &&
            candidate.NotBefore is { } notBefore &&
            candidate.ExpiresAt is { } expiresAt &&
            issuedAt <= now + options.ClockSkew &&
            expiresAt > issuedAt &&
            expiresAt > notBefore &&
            expiresAt - issuedAt <= Profile.MaximumLifetime;
        if (!valid)
        {
            return false;
        }

        token = candidate;
        return true;
    }

    private bool SatisfiesSubjectRule(JsonWebToken token) =>
        Profile.ExpectedSubject is not null ||
        !Profile.RequireSubject ||
        token.Subject is { Value.Length: > 0 };

    private static bool VerifySignature(JsonWebToken token, JsonWebKey key)
    {
        if (!string.Equals(token.Algorithm, JoseAlgorithms.ES256, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = Base64Url.DecodeFromChars(token.Parts!.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] signingInput = Encoding.ASCII.GetBytes(token.SigningInput!);
        try
        {
            using ECDsa publicKey = key.CreateECDsa();
            var verifier = new EcdsaJsonWebTokenSignatureVerifier(publicKey, key.KeyId);
            return verifier.CanVerify(JoseAlgorithms.ES256, token.Header.KeyId) &&
                verifier.Verify(JoseAlgorithms.ES256, signingInput, signature);
        }
        catch (Exception exception) when (
            exception is ArgumentException or CryptographicException or FormatException or
                InvalidOperationException or PlatformNotSupportedException)
        {
            // An unusable trusted key (bad coordinates, an off-curve point, or a non-JOSE curve)
            // cannot verify anything; it rejects the token rather than escaping to the caller.
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingInput);
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}
