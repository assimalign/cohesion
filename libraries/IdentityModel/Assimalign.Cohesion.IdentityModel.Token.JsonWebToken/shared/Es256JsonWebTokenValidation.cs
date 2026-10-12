using System;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

// Deviates from the RootNamespace rule: shared source keeps its owning namespace in each consumer.
namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

/// <summary>
/// Applies the shared ES256 trust, signature, and claim rules without owning verification state.
/// </summary>
internal static class Es256JsonWebTokenValidation
{
    // A supplied verifier borrows the selected profile key from its caller. The caller owns
    // that key's lifetime and synchronizes verification against disposal; null retains the
    // public validator's per-call key import and disposal behavior.
    internal static bool TryValidate(
        JsonWebTokenValidationProfile profile,
        string compactToken,
        DateTimeOffset now,
        IJsonWebTokenSignatureVerifier? signatureVerifier,
        [NotNullWhen(true)] out JsonWebToken? token)
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

        if (profile.ExpectedSubject is { } expectedSubject &&
            !string.Equals(candidate.Subject?.Value, expectedSubject, StringComparison.Ordinal))
        {
            return false;
        }

        JsonWebKey? key = profile.IssuerKeys.Invoke(candidate.Issuer)?.Find(candidate.Header.KeyId);
        if (key is null || !VerifySignature(candidate, key, signatureVerifier))
        {
            return false;
        }

        var options = new JsonWebTokenValidationOptions(now)
        {
            ClockSkew = profile.ClockSkew,
            ExpectedIssuer = candidate.Issuer,
            AllowUnsecured = false,
        };
        options.AllowedAlgorithms.Add(JoseAlgorithms.ES256);
        options.RequiredClaims.Add("iss");
        options.RequiredClaims.Add("sub");
        options.RequiredClaims.Add("aud");
        options.RequiredClaims.Add("exp");
        options.RequiredClaims.Add("nbf");
        options.RequiredClaims.Add("iat");
        options.RequiredClaims.Add("jti");

        bool valid = candidate.Validate(options).Succeeded &&
            SatisfiesSubjectRule(profile, candidate) &&
            !string.IsNullOrWhiteSpace(candidate.Id) &&
            candidate.IssuedAt is { } issuedAt &&
            candidate.NotBefore is { } notBefore &&
            candidate.ExpiresAt is { } expiresAt &&
            issuedAt <= now + options.ClockSkew &&
            expiresAt > issuedAt &&
            expiresAt > notBefore &&
            expiresAt - issuedAt <= profile.MaximumLifetime;
        if (!valid)
        {
            return false;
        }

        token = candidate;
        return true;
    }

    private static bool SatisfiesSubjectRule(JsonWebTokenValidationProfile profile, JsonWebToken token) =>
        profile.ExpectedSubject is not null ||
        !profile.RequireSubject ||
        token.Subject is { Value.Length: > 0 };

    private static bool VerifySignature(
        JsonWebToken token,
        JsonWebKey key,
        IJsonWebTokenSignatureVerifier? signatureVerifier)
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
            if (signatureVerifier is not null)
            {
                return signatureVerifier.CanVerify(JoseAlgorithms.ES256, token.Header.KeyId) &&
                    signatureVerifier.Verify(JoseAlgorithms.ES256, signingInput, signature);
            }

            using ECDsa publicKey = key.CreateECDsa();
            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(publicKey, key.KeyId);
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
