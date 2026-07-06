using System;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an immutable normalized claim about an identity subject.
/// </summary>
public sealed class IdentityClaim : IIdentityClaim
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityClaim" /> class.
    /// </summary>
    /// <param name="type">The canonical claim type.</param>
    /// <param name="value">The typed claim value. Must not be undefined.</param>
    /// <param name="issuer">The party that asserted the claim.</param>
    /// <param name="provenance">The protocol provenance of the claim.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="type" /> is null or whitespace, or when
    /// <paramref name="value" /> is undefined.
    /// </exception>
    public IdentityClaim(
        string type,
        IdentityClaimValue value,
        string? issuer = null,
        IdentityClaimProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (value.IsUndefined)
        {
            throw new ArgumentException(
                $"The claim '{type}' must not have an undefined value. Use IdentityClaimValue.Null for explicit null.",
                nameof(value));
        }

        Type = type;
        Value = value;
        Issuer = issuer;
        Provenance = provenance;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public IdentityClaimValue Value { get; }

    /// <inheritdoc />
    public string? Issuer { get; }

    /// <inheritdoc />
    public IdentityClaimProvenance? Provenance { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Type}: {Value}";
}
