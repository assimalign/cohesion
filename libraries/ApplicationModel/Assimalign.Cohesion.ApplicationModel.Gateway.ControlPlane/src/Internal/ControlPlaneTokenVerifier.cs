using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane.Internal;

internal static class ControlPlaneTokenVerifier
{
    private const string Audience = "cohesion-export";
    private const string TokenUseClaim = "cohesion_token_use";
    private const string GatewayTokenUse = "gateway";
    private static readonly TimeSpan _maximumLifetime = TimeSpan.FromHours(8);

    public static bool TryVerify(
        string compactToken,
        IReadOnlyList<TrustedIssuer> trustedIssuers,
        DateTimeOffset now,
        out ControlPlanePrincipal principal)
    {
        principal = default;
        if (!JsonWebToken.TryParse(compactToken, out JsonWebToken? token) ||
            token is null ||
            token.Issuer is null ||
            token.Algorithm is null ||
            token.SigningInput is null ||
            token.Parts is null)
        {
            return false;
        }

        TrustedIssuer? issuer = FindIssuer(token.Issuer, trustedIssuers);
        if (issuer is null || !VerifySignature(token, issuer.PublicKey))
        {
            return false;
        }

        var options = new JsonWebTokenValidationOptions(now)
        {
            ExpectedIssuer = issuer.Issuer,
            ExpectedAudience = Audience,
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
            expiresAt - issuedAt <= _maximumLifetime;
        if (!valid)
        {
            return false;
        }

        principal = new ControlPlanePrincipal(
            token.Issuer,
            token.Subject!.Value,
            string.Equals(
                token.Claims.GetString(TokenUseClaim),
                GatewayTokenUse,
                StringComparison.Ordinal),
            issuer.AllowedCommandKinds);
        return true;
    }

    private static TrustedIssuer? FindIssuer(
        string issuerName,
        IReadOnlyList<TrustedIssuer> trustedIssuers)
    {
        for (int index = 0; index < trustedIssuers.Count; index++)
        {
            TrustedIssuer issuer = trustedIssuers[index];
            if (string.Equals(issuer.Issuer, issuerName, StringComparison.Ordinal))
            {
                return issuer;
            }
        }

        return null;
    }

    private static bool VerifySignature(JsonWebToken token, JsonElement publicKey)
    {
        byte[] x;
        byte[] y;
        byte[] signature;
        try
        {
            x = Base64Url.DecodeFromChars(publicKey.GetProperty("x").GetString()!);
            y = Base64Url.DecodeFromChars(publicKey.GetProperty("y").GetString()!);
            signature = Base64Url.DecodeFromChars(token.Parts!.Signature);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }

        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = x,
                    Y = y,
                },
            });
            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(
                key,
                publicKey.GetProperty("kid").GetString());
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

internal readonly record struct ControlPlanePrincipal(
    string Issuer,
    string Subject,
    bool CanDispatchCommands,
    IReadOnlyList<string> AllowedCommandKinds);
