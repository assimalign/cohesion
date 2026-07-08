using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Verifies HMAC-signed tokens (<c>HS256</c>/<c>HS384</c>/<c>HS512</c>) with a shared secret.
/// The computed MAC is compared in fixed time to defeat timing side channels.
/// </summary>
internal sealed class HmacJwtSignatureVerifier : IJwtSignatureVerifier
{
    private readonly byte[] _key;
    private readonly string? _keyId;

    public HmacJwtSignatureVerifier(byte[] key, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
        {
            throw new ArgumentException("The HMAC key must not be empty.", nameof(key));
        }

        _key = (byte[])key.Clone();
        _keyId = keyId;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
    {
        if (!JwtSignatureAlgorithms.IsHmac(algorithm))
        {
            return false;
        }

        return _keyId is null || string.Equals(_keyId, keyId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        Span<byte> computed = stackalloc byte[64]; // large enough for SHA-512
        int written = algorithm switch
        {
            JoseAlgorithms.HS256 => HMACSHA256.HashData(_key, signingInput, computed),
            JoseAlgorithms.HS384 => HMACSHA384.HashData(_key, signingInput, computed),
            JoseAlgorithms.HS512 => HMACSHA512.HashData(_key, signingInput, computed),
            _ => -1,
        };

        if (written <= 0)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed[..written], signature);
    }
}
