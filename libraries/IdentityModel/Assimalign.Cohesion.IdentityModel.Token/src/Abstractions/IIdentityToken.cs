using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents an identity token independent of its wire format.
/// </summary>
public interface IIdentityToken
{
    /// <summary>
    /// Gets the token wire format.
    /// </summary>
    IdentityTokenKind Kind { get; }

    /// <summary>
    /// Gets the token identifier.
    /// </summary>
    string? Id { get; }

    /// <summary>
    /// Gets the logical subject represented by the token.
    /// </summary>
    string? Subject { get; }

    /// <summary>
    /// Gets the token issuer.
    /// </summary>
    string? Issuer { get; }

    /// <summary>
    /// Gets the declared token type.
    /// </summary>
    string? TokenType { get; }

    /// <summary>
    /// Gets the original token payload.
    /// </summary>
    string? RawData { get; }

    /// <summary>
    /// Gets the timestamp when the token was issued.
    /// </summary>
    DateTimeOffset? IssuedAt { get; }

    /// <summary>
    /// Gets the timestamp when the token becomes valid.
    /// </summary>
    DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the timestamp when the token expires.
    /// </summary>
    DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets the audiences targeted by the token.
    /// </summary>
    IReadOnlyList<string> Audiences { get; }

    /// <summary>
    /// Gets the normalized claims carried by the token.
    /// </summary>
    IReadOnlyList<IIdentityTokenClaim> Claims { get; }

    /// <summary>
    /// Gets additional token properties that are not normalized into first-class members.
    /// </summary>
    IReadOnlyDictionary<string, object?> Properties { get; }

    /// <summary>
    /// Determines whether the token is intended for the provided audience.
    /// </summary>
    /// <param name="audience">The audience value to match.</param>
    /// <returns><see langword="true" /> when the audience exists; otherwise <see langword="false" />.</returns>
    bool HasAudience(string audience);

    /// <summary>
    /// Gets every claim that matches the provided type.
    /// </summary>
    /// <param name="claimType">The claim type to match.</param>
    /// <returns>The matching claims.</returns>
    IReadOnlyList<IIdentityTokenClaim> GetClaims(string claimType);

    /// <summary>
    /// Attempts to locate the first claim with the provided type.
    /// </summary>
    /// <param name="claimType">The claim type to match.</param>
    /// <param name="claim">When this method returns, contains the first matching claim, if one exists.</param>
    /// <returns><see langword="true" /> when a matching claim exists; otherwise <see langword="false" />.</returns>
    bool TryGetClaim(string claimType, out IIdentityTokenClaim? claim);
}
