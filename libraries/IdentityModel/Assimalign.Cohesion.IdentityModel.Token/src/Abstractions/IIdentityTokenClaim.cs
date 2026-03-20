namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Represents a normalized claim carried by an identity token.
/// </summary>
public interface IIdentityTokenClaim
{
    /// <summary>
    /// Gets the claim type.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Gets the raw claim value.
    /// </summary>
    object? Value { get; }

    /// <summary>
    /// Gets the normalized value shape.
    /// </summary>
    IdentityTokenValueKind ValueKind { get; }

    /// <summary>
    /// Gets the optional wire-level value type.
    /// </summary>
    string? ValueType { get; }

    /// <summary>
    /// Gets the issuer that produced the claim.
    /// </summary>
    string? Issuer { get; }

    /// <summary>
    /// Gets the original issuer that produced the claim.
    /// </summary>
    string? OriginalIssuer { get; }
}
