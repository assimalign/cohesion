using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents an identity token or assertion normalized into the canonical identity model,
/// independent of its wire format. This is the protocol-neutral layer between the root
/// canonical contracts and the concrete JWT and SAML token packages: a materialized token
/// exposes the same subject, claim, temporal, and authentication-context surfaces regardless
/// of whether it was a JWT or a SAML assertion.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Claims" /> is the authoritative normalized claim record — the complete set of
/// claims the token asserts, carrying provenance. The typed first-class members
/// (<see cref="Subject" />, <see cref="Issuer" />, <see cref="Audiences" />, the temporal
/// members, and <see cref="AuthenticationContext" />) are the <em>normalized projection</em>
/// of that record: a concrete token package (JWT, SAML) is responsible for populating them
/// consistently with <see cref="Claims" />. Consumers read the typed members for the common
/// case and fall back to <see cref="Claims" /> for the full record.
/// </para>
/// <para>
/// The interface is data-only. Audience membership and temporal checks are
/// <c>extension(IIdentityToken)</c> members; claim lookup uses the root
/// <see cref="IIdentityClaimCollection" /> vocabulary (<c>Contains</c>/<c>TryGet</c>/
/// <c>GetAll</c>/<c>GetValues</c>) on <see cref="Claims" />, so the family ships one spelling
/// for "find claims by type".
/// </para>
/// </remarks>
public interface IIdentityToken
{
    /// <summary>
    /// Gets the token document format.
    /// </summary>
    IdentityTokenKind Kind { get; }

    /// <summary>
    /// Gets the protocol that produced the token (provenance): a JSON Web Token may be an
    /// OpenID Connect ID token (<c>oidc</c>) or an OAuth access token (<c>oauth2</c>); a SAML
    /// assertion is always <c>saml2</c>.
    /// </summary>
    AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the token identifier (a JWT <c>jti</c> / a SAML assertion id). Feeds
    /// <see cref="AuthenticationResult" /> evidence linkage.
    /// </summary>
    string? Id { get; }

    /// <summary>
    /// Gets the normalized subject the token is about, or <see langword="null" /> when the
    /// token names no subject. The raw wire subject survives as
    /// <see cref="SubjectIdentifier.Value" />.
    /// </summary>
    SubjectIdentifier? Subject { get; }

    /// <summary>
    /// Gets the identifier of the party that <em>issued</em> the token (an OpenID Connect
    /// <c>iss</c> / a SAML <c>Assertion/Issuer</c>), as the exact wire string. This is the
    /// asserting issuer; it is distinct from <see cref="SubjectIdentifier.Issuer" /> on
    /// <see cref="Subject" />, which is the subject-scoping qualifier (a SAML
    /// <c>NameQualifier</c>) and MAY differ. Use this member to answer "who issued this
    /// token"; use <see cref="Subject" /> for identity-scope comparison.
    /// </summary>
    string? Issuer { get; }

    /// <summary>
    /// Gets the declared token type (a JWT <c>typ</c> header / an <c>OAuth token_type</c>).
    /// </summary>
    string? TokenType { get; }

    /// <summary>
    /// Gets the audiences the token is intended for, as exact wire strings. Compare
    /// ordinally.
    /// </summary>
    IReadOnlyList<string> Audiences { get; }

    /// <summary>
    /// Gets the instant the token was issued (a JWT <c>iat</c> / a SAML <c>IssueInstant</c>).
    /// </summary>
    DateTimeOffset? IssuedAt { get; }

    /// <summary>
    /// Gets the instant at or after which the token becomes valid (a JWT <c>nbf</c> / a SAML
    /// <c>Conditions/@NotBefore</c>). This is the token's primary validity window start.
    /// </summary>
    DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the instant at or after which the token expires (a JWT <c>exp</c> / a SAML
    /// <c>Conditions/@NotOnOrAfter</c>). This is the token's primary validity window end;
    /// format-specific windows (a SAML bearer confirmation window) are the concrete token
    /// package's concern.
    /// </summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets how and when the subject authenticated (OIDC <c>auth_time</c>/<c>acr</c>/
    /// <c>amr</c>/<c>sid</c>, SAML authentication statement content), when the token carries
    /// it; otherwise <see langword="null" />. A normalized projection of the corresponding
    /// claims.
    /// </summary>
    AuthenticationContext? AuthenticationContext { get; }

    /// <summary>
    /// Gets the authoritative normalized claim record the token asserts, with provenance.
    /// </summary>
    IIdentityClaimCollection Claims { get; }

    /// <summary>
    /// Gets additional token detail not projected onto a first-class member, as typed
    /// values.
    /// </summary>
    IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <summary>
    /// Gets the original token payload (JWT compact serialization / SAML assertion XML),
    /// preserved for signature re-verification and correlation back to the wire shape. Null
    /// when not retained.
    /// </summary>
    string? RawData { get; }
}
