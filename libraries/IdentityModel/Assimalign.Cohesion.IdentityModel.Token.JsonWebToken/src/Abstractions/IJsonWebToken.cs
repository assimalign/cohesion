using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Represents a JSON Web Token: a normalized identity token with a typed JOSE header and the
/// compact-serialization detail a signature verifier needs.
/// </summary>
public interface IJsonWebToken : IIdentityToken
{
    /// <summary>
    /// Gets the typed JOSE header.
    /// </summary>
    JoseHeader Header { get; }

    /// <summary>
    /// Gets the declared signing algorithm (<c>alg</c>). Alias of
    /// <see cref="JoseHeader.Algorithm" />.
    /// </summary>
    string? Algorithm { get; }

    /// <summary>
    /// Gets the compact serialization segments, when available.
    /// </summary>
    JsonWebTokenParts? Parts { get; }

    /// <summary>
    /// Gets the JWS signing input (<c>header.payload</c> using the encoded segments as
    /// received), when a compact form is available. This is the seam for a Security-layer
    /// signature verifier; this package deliberately does not verify the signature, so a
    /// successful <c>Validate</c> means "data and hash rules passed", never "signature
    /// verified".
    /// </summary>
    string? SigningInput { get; }
}
