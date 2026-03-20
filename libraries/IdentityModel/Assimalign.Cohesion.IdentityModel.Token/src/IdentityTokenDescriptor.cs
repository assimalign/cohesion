using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Describes the normalized contents of an identity token before it is materialized.
/// </summary>
public class IdentityTokenDescriptor
{
    /// <summary>
    /// Gets or sets the token identifier.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Gets or sets the logical subject represented by the token.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Gets or sets the token issuer.
    /// </summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Gets or sets the declared token type.
    /// </summary>
    public string? TokenType { get; set; }

    /// <summary>
    /// Gets or sets the original wire representation of the token.
    /// </summary>
    public string? RawData { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the token was issued.
    /// </summary>
    public DateTimeOffset? IssuedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the token becomes valid.
    /// </summary>
    public DateTimeOffset? NotBefore { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the token expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Gets the audiences targeted by the token.
    /// </summary>
    public IList<string> Audiences { get; } = new List<string>();

    /// <summary>
    /// Gets the claims carried by the token.
    /// </summary>
    public IList<IdentityTokenClaim> Claims { get; } = new List<IdentityTokenClaim>();

    /// <summary>
    /// Gets additional token properties that are not normalized into first-class members.
    /// </summary>
    public IDictionary<string, object?> Properties { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}
