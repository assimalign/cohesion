using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Creates JSON Web Token writers backed by BCL asymmetric cryptography.
/// </summary>
public static class JsonWebTokenWriter
{
    /// <summary>
    /// Creates an ES256 writer that borrows the supplied ECDSA P-256 private key.
    /// </summary>
    /// <param name="privateKey">
    /// The ECDSA P-256 private key. The writer does not dispose it; the caller must keep it
    /// alive for the writer's lifetime.
    /// </param>
    /// <param name="keyId">The non-empty <c>kid</c> written to every JOSE header.</param>
    /// <returns>An ES256 compact-token writer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="privateKey" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyId" /> is empty, or <paramref name="privateKey" /> does not use the
    /// NIST P-256 curve.
    /// </exception>
    public static IJsonWebTokenWriter CreateEs256(ECDsa privateKey, string keyId)
        => new Es256JsonWebTokenWriter(privateKey, keyId);
}
