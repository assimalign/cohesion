using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents a lightweight reference to a party participating in an authentication
/// protocol: who the party is and which role it plays. The full description of a party
/// (endpoints, keys, capabilities) is <see cref="ProtocolMetadata" />; a
/// <see cref="ProtocolParty" /> is the reference used by trust registries and diagnostics.
/// </summary>
/// <remarks>
/// Equality is defined over <see cref="Identifier" /> and <see cref="Role" /> with ordinal
/// comparison; <see cref="DisplayName" /> and <see cref="Properties" /> are descriptive
/// detail and never participate in equality. A party deliberately carries no protocol
/// member: one entity identifier can serve the same party across protocols, and protocol
/// provenance lives on metadata and messages.
/// </remarks>
public sealed class ProtocolParty : IEquatable<ProtocolParty>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolParty" /> class.
    /// </summary>
    /// <param name="identifier">The party identifier (entity ID, issuer URL, or client identifier).</param>
    /// <param name="role">The role the party plays.</param>
    /// <param name="displayName">The optional human-readable party name.</param>
    /// <param name="properties">Additional party detail. The dictionary is snapshotted; keys compare ordinally.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="identifier" /> is null or whitespace, or when a property
    /// name is blank or a property value is undefined.
    /// </exception>
    public ProtocolParty(
        string identifier,
        ProtocolRole role,
        string? displayName = null,
        IReadOnlyDictionary<string, IdentityClaimValue>? properties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        Identifier = identifier;
        Role = role;
        DisplayName = displayName;
        Properties = ModelSnapshot.Properties(properties, nameof(properties));
    }

    /// <summary>
    /// Gets the party identifier (entity ID, issuer URL, or client identifier).
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Gets the role the party plays.
    /// </summary>
    public ProtocolRole Role { get; }

    /// <summary>
    /// Gets the optional human-readable party name.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>
    /// Gets additional party detail. Never participates in equality.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public bool Equals(ProtocolParty? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Identifier, other.Identifier, StringComparison.Ordinal)
            && Role.Equals(other.Role);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolParty other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Identifier), Role.GetHashCode());

    /// <summary>
    /// Determines whether two parties are equal.
    /// </summary>
    /// <param name="left">The first party.</param>
    /// <param name="right">The second party.</param>
    /// <returns><see langword="true" /> when the parties are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(ProtocolParty? left, ProtocolParty? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>
    /// Determines whether two parties are unequal.
    /// </summary>
    /// <param name="left">The first party.</param>
    /// <param name="right">The second party.</param>
    /// <returns><see langword="true" /> when the parties are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(ProtocolParty? left, ProtocolParty? right) => !(left == right);

    /// <inheritdoc />
    public override string ToString() => $"{Identifier} ({Role})";
}
