namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents the document format of an identity token. This is the token's <em>wire
/// shape</em> (a compact JWT, a SAML assertion), distinct from the
/// <see cref="AuthenticationProtocol" /> that produced it — a JSON Web Token can carry an
/// OpenID Connect ID token or an OAuth access token.
/// </summary>
public enum IdentityTokenKind
{
    /// <summary>
    /// The token format is unknown. This is the fail-closed default: a token whose format
    /// was never assigned must not read as a recognized format.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A compact JSON Web Token.
    /// </summary>
    JsonWebToken = 1,

    /// <summary>
    /// A SAML assertion token.
    /// </summary>
    Saml = 2
}
