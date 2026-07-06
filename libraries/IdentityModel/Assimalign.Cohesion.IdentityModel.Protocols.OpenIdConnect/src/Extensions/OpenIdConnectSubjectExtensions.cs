namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// The single, pinned recipe for lifting an OpenID Connect wire subject into the canonical
/// <see cref="SubjectIdentifier" />. Both the login leg (ID token) and the logout leg
/// (logout token) must derive identifiers through this recipe — wire fields only — or
/// single-logout correlation silently fails, because <see cref="SubjectIdentifier" />
/// equality spans every scope field. Sector or format enrichment is a consumer policy that
/// must be applied identically on both legs or not at all.
/// </summary>
public static class OpenIdConnectSubjectExtensions
{
    extension(OpenIdConnectIdToken token)
    {
        /// <summary>
        /// Derives the canonical subject identifier from the token's wire fields:
        /// value = <c>sub</c>, issuer = <c>iss</c>, unspecified format, no relying-party
        /// qualifier.
        /// </summary>
        /// <returns>The canonical identifier, or null when the token has no subject.</returns>
        public SubjectIdentifier? GetSubjectIdentifier()
        {
            return token.Subject is null
                ? null
                : new SubjectIdentifier(token.Subject, issuer: token.Issuer);
        }
    }

    extension(OpenIdConnectLogoutToken token)
    {
        /// <summary>
        /// Derives the canonical subject identifier from the token's wire fields:
        /// value = <c>sub</c>, issuer = <c>iss</c>, unspecified format, no relying-party
        /// qualifier.
        /// </summary>
        /// <returns>The canonical identifier, or null when the token has no subject.</returns>
        public SubjectIdentifier? GetSubjectIdentifier()
        {
            return token.Subject is null
                ? null
                : new SubjectIdentifier(token.Subject, issuer: token.Issuer);
        }
    }
}
