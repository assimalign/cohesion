using Assimalign.Cohesion.IdentityModel.Token;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Describes the contents of a JSON Web Token before it is materialized. The JOSE header is a
/// typed <see cref="JoseHeaderDescriptor" /> — the object?-typed header bag the previous shape
/// carried was removed when the normalization layer moved to the canonical value model.
/// </summary>
public sealed class JsonWebTokenDescriptor : IdentityTokenDescriptor
{
    /// <summary>
    /// Gets the JOSE header.
    /// </summary>
    public JoseHeaderDescriptor Header { get; } = new();

    /// <summary>
    /// Gets or sets the compact serialization segments, when the token was parsed from or is
    /// paired with a compact form.
    /// </summary>
    public JsonWebTokenParts? Parts { get; set; }
}
