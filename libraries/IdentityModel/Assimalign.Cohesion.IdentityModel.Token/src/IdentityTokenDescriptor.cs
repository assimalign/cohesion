using System;
using System.Collections.Generic;

using Assimalign.Cohesion.IdentityModel;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Describes the normalized contents of an identity token before it is materialized into an
/// immutable <see cref="IdentityToken" /> derivative. The document format
/// (<see cref="IdentityTokenKind" />) is pinned by the concrete token type through the base
/// constructor, not carried here — a descriptor never claims a format that contradicts the
/// type that materializes it.
/// </summary>
/// <remarks>
/// <see cref="Subject" /> and <see cref="AuthenticationContext" /> are the root canonical
/// types, built by the descriptor author (the concrete token package) so the token's
/// normalized subject and authentication context stay consistent with <see cref="Claims" />.
/// The author sets <see cref="Subject" />'s issuer/qualifier and format explicitly, because
/// the subject-scoping qualifier (a SAML <c>NameQualifier</c>) may differ from the token
/// <see cref="Issuer" />.
/// </remarks>
public class IdentityTokenDescriptor
{
    /// <summary>
    /// Gets or sets the protocol that produced the token. Defaults to
    /// <see cref="AuthenticationProtocol.Unknown" />.
    /// </summary>
    public AuthenticationProtocol Protocol { get; set; }

    /// <summary>
    /// Gets or sets the token identifier (a JWT <c>jti</c> / a SAML assertion id).
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the normalized subject the token is about. Leave <see langword="null" />
    /// when the token names no subject (<see cref="SubjectIdentifier" /> cannot hold an empty
    /// value).
    /// </summary>
    public SubjectIdentifier? Subject { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the party that issued the token (an OpenID Connect
    /// <c>iss</c> / a SAML <c>Assertion/Issuer</c>).
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the declared token type (a JWT <c>typ</c> header / an OAuth
    /// <c>token_type</c>).
    /// </summary>
    public string? TokenType { get; set; }

    /// <summary>
    /// Gets or sets the original wire representation of the token.
    /// </summary>
    public string? RawData { get; set; }

    /// <summary>
    /// Gets or sets the instant the token was issued.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the instant at or after which the token becomes valid.
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// Gets or sets the instant at or after which the token expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets or sets how and when the subject authenticated. Built by the descriptor author
    /// through an <see cref="AuthenticationContextDescriptor" />; the base snapshots the
    /// reference.
    /// </summary>
    public AuthenticationContext? AuthenticationContext { get; set; }

    /// <summary>
    /// Gets the audiences the token is intended for.
    /// </summary>
    public IList<string> Audiences { get; } = new List<string>();

    /// <summary>
    /// Gets the normalized claims the token asserts, as root canonical claims. An absent
    /// value must be <see cref="IdentityClaimValue.Null" /> — never the undefined default.
    /// </summary>
    public IList<IIdentityClaim> Claims { get; } = new List<IIdentityClaim>();

    /// <summary>
    /// Gets additional token detail not projected onto a first-class member, as typed
    /// values.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
