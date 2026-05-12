namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents the wire format of an identity token.
/// </summary>
public enum IdentityTokenKind
{
    /// <summary>
    /// A compact JSON Web Token.
    /// </summary>
    JsonWebToken,

    /// <summary>
    /// A SAML assertion token.
    /// </summary>
    Saml
}
