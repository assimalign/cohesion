using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.ControlPlane;

internal sealed class BootstrapTokenVerifier : IDisposable
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    private readonly string _application;
    private readonly string _gateway;
    private readonly ECDsa _key;
    private readonly string _keyId;

    internal BootstrapTokenVerifier(ResourceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _application = context.ApplicationName ?? throw new InvalidOperationException(
            "A gateway-managed resource requires an application name.");
        _gateway = context.GatewayName ?? throw new InvalidOperationException(
            "A gateway-managed resource requires a gateway name.");
        if (context.ApplicationTrustKey.IsEmpty)
        {
            throw new InvalidOperationException(
                "A gateway-managed resource requires a public application trust key.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(context.ApplicationTrustKey);
            JsonElement jwk = document.RootElement;
            if (jwk.GetProperty("kty").GetString() != "EC" ||
                jwk.GetProperty("crv").GetString() != "P-256")
            {
                throw new InvalidOperationException("The application trust key must be an EC P-256 JWK.");
            }

            _keyId = jwk.GetProperty("kid").GetString()
                ?? throw new InvalidOperationException("The application trust key requires a kid.");
            byte[] x = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()!);
            byte[] y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()!);
            try
            {
                _key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y },
                });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(x);
                CryptographicOperations.ZeroMemory(y);
            }
        }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The application trust key is not a valid EC P-256 JWK.",
                exception);
        }
    }

    internal BootstrapTokenStatus Validate(
        string compactToken,
        string expectedAudience,
        DateTimeOffset now)
    {
        if (!JsonWebToken.TryParse(compactToken, out JsonWebToken? token) ||
            token is null ||
            token.SigningInput is null ||
            token.Parts is null ||
            !string.Equals(token.Issuer, _application, StringComparison.Ordinal) ||
            !string.Equals(token.Subject?.Value, _gateway, StringComparison.Ordinal) ||
            !VerifySignature(token))
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        var options = new JsonWebTokenValidationOptions(now)
        {
            ExpectedIssuer = _application,
            AllowUnsecured = false,
        };
        options.AllowedAlgorithms.Add("ES256");
        options.RequiredClaims.Add("iss");
        options.RequiredClaims.Add("sub");
        options.RequiredClaims.Add("aud");
        options.RequiredClaims.Add("exp");
        options.RequiredClaims.Add("nbf");
        options.RequiredClaims.Add("iat");
        options.RequiredClaims.Add("jti");
        bool valid = token.Validate(options).Succeeded &&
            token.IssuedAt is { } issuedAt &&
            token.NotBefore is { } notBefore &&
            token.ExpiresAt is { } expiresAt &&
            issuedAt <= now + options.ClockSkew &&
            expiresAt > issuedAt &&
            expiresAt > notBefore &&
            expiresAt - issuedAt <= MaximumLifetime &&
            !string.IsNullOrWhiteSpace(token.Id);
        if (!valid)
        {
            return BootstrapTokenStatus.Unauthorized;
        }

        for (int index = 0; index < token.Audiences.Count; index++)
        {
            if (string.Equals(token.Audiences[index], expectedAudience, StringComparison.Ordinal))
            {
                return BootstrapTokenStatus.Authorized;
            }
        }

        return BootstrapTokenStatus.Forbidden;
    }

    public void Dispose() => _key.Dispose();

    private bool VerifySignature(JsonWebToken token)
    {
        if (!string.Equals(token.Algorithm, "ES256", StringComparison.Ordinal) ||
            !string.Equals(token.Header.KeyId, _keyId, StringComparison.Ordinal))
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
            IJsonWebTokenSignatureVerifier verifier = JsonWebTokenSignatureVerifier.CreateEcdsa(_key, _keyId);
            return verifier.CanVerify(token.Algorithm!, token.Header.KeyId) &&
                verifier.Verify(token.Algorithm!, signingInput, signature);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingInput);
            CryptographicOperations.ZeroMemory(signature);
        }
    }
}

internal enum BootstrapTokenStatus
{
    Unauthorized,
    Forbidden,
    Authorized,
}
