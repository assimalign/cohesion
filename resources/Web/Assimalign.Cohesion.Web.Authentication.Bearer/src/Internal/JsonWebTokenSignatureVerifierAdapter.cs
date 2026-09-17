using System;

using IdentityModelSignatureVerifier = Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.IJsonWebTokenSignatureVerifier;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Adapts the reusable IdentityModel asymmetric verifier to Bearer's existing public seam.
/// </summary>
internal sealed class JsonWebTokenSignatureVerifierAdapter : IJwtSignatureVerifier
{
    private readonly IdentityModelSignatureVerifier _verifier;

    public JsonWebTokenSignatureVerifierAdapter(IdentityModelSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

    /// <inheritdoc />
    public bool CanVerify(string algorithm, string? keyId) => _verifier.CanVerify(algorithm, keyId);

    /// <inheritdoc />
    public bool Verify(string algorithm, ReadOnlySpan<byte> signingInput, ReadOnlySpan<byte> signature)
        => _verifier.Verify(algorithm, signingInput, signature);
}
