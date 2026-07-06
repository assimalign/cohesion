namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Defines the starter canonical claim-type vocabulary. The canonical vocabulary adopts the
/// IANA-registered JWT claim names, because that registry is the only cross-vendor claim
/// vocabulary in wide use; SAML attribute names map onto these canonical types through
/// <see cref="IdentityClaimMappings" /> and the <c>Canonicalize</c> extension on
/// <see cref="IIdentityClaimCollection" />, with the original names preserved in
/// <see cref="IdentityClaimProvenance" />.
/// </summary>
public static class IdentityClaimTypes
{
    /// <summary>
    /// The subject identifier claim (<c>sub</c>).
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// The issuer claim (<c>iss</c>, RFC 7519).
    /// </summary>
    public const string Issuer = "iss";

    /// <summary>
    /// The audience claim (<c>aud</c>, RFC 7519).
    /// </summary>
    public const string Audience = "aud";

    /// <summary>
    /// The expiration time claim (<c>exp</c>, RFC 7519).
    /// </summary>
    public const string ExpirationTime = "exp";

    /// <summary>
    /// The issued-at claim (<c>iat</c>, RFC 7519).
    /// </summary>
    public const string IssuedAt = "iat";

    /// <summary>
    /// The not-before claim (<c>nbf</c>, RFC 7519).
    /// </summary>
    public const string NotBefore = "nbf";

    /// <summary>
    /// The token identifier claim (<c>jti</c>, RFC 7519).
    /// </summary>
    public const string JwtId = "jti";

    /// <summary>
    /// The full display name claim (<c>name</c>).
    /// </summary>
    public const string Name = "name";

    /// <summary>
    /// The given (first) name claim (<c>given_name</c>).
    /// </summary>
    public const string GivenName = "given_name";

    /// <summary>
    /// The family (last) name claim (<c>family_name</c>).
    /// </summary>
    public const string FamilyName = "family_name";

    /// <summary>
    /// The middle name claim (<c>middle_name</c>).
    /// </summary>
    public const string MiddleName = "middle_name";

    /// <summary>
    /// The preferred username claim (<c>preferred_username</c>).
    /// </summary>
    public const string PreferredUsername = "preferred_username";

    /// <summary>
    /// The email address claim (<c>email</c>).
    /// </summary>
    public const string Email = "email";

    /// <summary>
    /// The email verification state claim (<c>email_verified</c>).
    /// </summary>
    public const string EmailVerified = "email_verified";

    /// <summary>
    /// The phone number claim (<c>phone_number</c>).
    /// </summary>
    public const string PhoneNumber = "phone_number";

    /// <summary>
    /// The phone number verification state claim (<c>phone_number_verified</c>).
    /// </summary>
    public const string PhoneNumberVerified = "phone_number_verified";

    /// <summary>
    /// The end-user locale claim (<c>locale</c>).
    /// </summary>
    public const string Locale = "locale";

    /// <summary>
    /// The end-user time zone claim (<c>zoneinfo</c>).
    /// </summary>
    public const string ZoneInfo = "zoneinfo";

    /// <summary>
    /// The roles claim (<c>roles</c>, RFC 9068).
    /// </summary>
    public const string Roles = "roles";

    /// <summary>
    /// The groups claim (<c>groups</c>, RFC 9068).
    /// </summary>
    public const string Groups = "groups";

    /// <summary>
    /// The entitlements claim (<c>entitlements</c>, RFC 9068).
    /// </summary>
    public const string Entitlements = "entitlements";

    /// <summary>
    /// The confirmation claim (<c>cnf</c>, RFC 7800). This is the canonical landing spot for
    /// proof-of-possession key bindings: OpenID Connect / OAuth <c>cnf</c> members and SAML
    /// holder-of-key confirmations both normalize onto this claim type, so downstream
    /// authorization can ask "is this principal proof-of-possession bound" without a
    /// protocol-specific convention.
    /// </summary>
    public const string Confirmation = "cnf";
}
