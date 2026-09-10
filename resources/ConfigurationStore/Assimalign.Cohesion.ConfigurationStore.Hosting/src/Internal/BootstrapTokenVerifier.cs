using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class BootstrapTokenVerifier
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    private readonly IReadOnlyList<ConfigurationTrustedIssuer> _trustedIssuers;

    internal BootstrapTokenVerifier(IReadOnlyList<ConfigurationTrustedIssuer> trustedIssuers)
    {
        _trustedIssuers = trustedIssuers ?? throw new ArgumentNullException(nameof(trustedIssuers));
    }

    internal BootstrapTokenValidation Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedAudience);

        if (!JsonWebToken.TryParse(compactToken, out JsonWebToken? token) ||
            token is null ||
            string.IsNullOrWhiteSpace(token.Issuer) ||
            string.IsNullOrWhiteSpace(token.Algorithm) ||
            token.SigningInput is null ||
            token.Parts is null)
        {
            return BootstrapTokenValidation.Unauthorized;
        }

        ConfigurationTrustedIssuer? issuer = FindIssuer(token.Issuer);
        if (issuer is null || !VerifySignature(token, issuer))
        {
            return BootstrapTokenValidation.Unauthorized;
        }

        var options = new JsonWebTokenValidationOptions(now)
        {
            ExpectedIssuer = issuer.Issuer,
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

        bool valid = token.Validate(options).Succeeded &&
            token.Subject is { Value.Length: > 0 } &&
            !string.IsNullOrWhiteSpace(token.Id) &&
            token.IssuedAt is { } issuedAt &&
            token.NotBefore is { } notBefore &&
            token.ExpiresAt is { } expiresAt &&
            issuedAt <= now + options.ClockSkew &&
            expiresAt > issuedAt &&
            expiresAt > notBefore &&
            expiresAt - issuedAt <= MaximumLifetime;
        if (!valid)
        {
            return BootstrapTokenValidation.Unauthorized;
        }

        for (int index = 0; index < token.Audiences.Count; index++)
        {
            if (string.Equals(token.Audiences[index], expectedAudience, StringComparison.Ordinal))
            {
                return new BootstrapTokenValidation(
                    BootstrapTokenValidationStatus.Authorized,
                    issuer.Issuer);
            }
        }

        return new BootstrapTokenValidation(
            BootstrapTokenValidationStatus.Forbidden,
            issuer.Issuer);
    }

    private ConfigurationTrustedIssuer? FindIssuer(string issuerName)
    {
        for (int index = 0; index < _trustedIssuers.Count; index++)
        {
            if (string.Equals(_trustedIssuers[index].Issuer, issuerName, StringComparison.Ordinal))
            {
                return _trustedIssuers[index];
            }
        }

        return null;
    }

    private static bool VerifySignature(
        JsonWebToken token,
        ConfigurationTrustedIssuer issuer)
    {
        byte[] x;
        byte[] y;
        byte[] signature;
        try
        {
            x = Base64Url.DecodeFromChars(issuer.PublicKey.GetProperty("x").GetString()!);
            y = Base64Url.DecodeFromChars(issuer.PublicKey.GetProperty("y").GetString()!);
            signature = Base64Url.DecodeFromChars(token.Parts!.Signature);
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            });
            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(
                key,
                issuer.KeyId);
            byte[] signingInput = Encoding.ASCII.GetBytes(token.SigningInput!);
            try
            {
                return verifier.CanVerify(token.Algorithm!, token.Header.KeyId) &&
                    verifier.Verify(token.Algorithm!, signingInput, signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signingInput);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(x);
            CryptographicOperations.ZeroMemory(y);
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}

internal readonly record struct BootstrapTokenValidation(
    BootstrapTokenValidationStatus Status,
    string? Issuer)
{
    internal static BootstrapTokenValidation Unauthorized =>
        new(BootstrapTokenValidationStatus.Unauthorized, null);
}

internal enum BootstrapTokenValidationStatus
{
    Unauthorized,
    Forbidden,
    Authorized,
}
