using System;
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Verifies JOSE RSA PKCS#1 and RSA-PSS signatures.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("RsaJsonWebTokenSignatureVerifier")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class RsaJsonWebTokenSignatureVerifier : IJsonWebTokenSignatureVerifier
{
    private readonly RSA _publicKey;
    private readonly string? _keyId;

    private RsaJsonWebTokenSignatureVerifier(RSA publicKey, string? keyId)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        _publicKey = publicKey;
        _keyId = keyId;
    }

    /// <summary>
    /// Creates a verifier for RSA PKCS#1 (<c>RS*</c>) and RSA-PSS (<c>PS*</c>) signatures.
    /// </summary>
    /// <param name="publicKey">
    /// The RSA verification key. The verifier does not dispose it; the caller must keep it
    /// alive for the verifier's lifetime.
    /// </param>
    /// <param name="keyId">The <c>kid</c> this key answers to, or <see langword="null" /> to match any key identifier.</param>
    /// <returns>An RSA JSON Web Token signature verifier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey" /> is <see langword="null" />.</exception>
    public static RsaJsonWebTokenSignatureVerifier Create(RSA publicKey, string? keyId = null)
        => new RsaJsonWebTokenSignatureVerifier(publicKey, keyId);

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId)
        => (JsonWebTokenSignatureAlgorithms.IsRsaPkcs1(algorithm) ||
            JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm)) &&
           (_keyId is null || string.Equals(_keyId, keyId, StringComparison.Ordinal));

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
    {
        if (!JsonWebTokenSignatureAlgorithms.IsRsaPkcs1(algorithm) &&
            !JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm))
        {
            return false;
        }

        HashAlgorithmName? hash = JsonWebTokenSignatureAlgorithms.GetHash(algorithm);
        if (hash is null)
        {
            return false;
        }

        RSASignaturePadding padding = JsonWebTokenSignatureAlgorithms.IsRsaPss(algorithm)
            ? RSASignaturePadding.Pss
            : RSASignaturePadding.Pkcs1;

        try
        {
            return _publicKey.VerifyData(signingInput, signature, hash.Value, padding);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private sealed class DebugView
    {
        public DebugView(RsaJsonWebTokenSignatureVerifier value)
        {
        }
    }
}
