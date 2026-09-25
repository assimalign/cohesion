using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the role a party plays in an authentication protocol, as an open, extensible
/// vocabulary. Well-known roles are exposed as static members; protocol branches add their
/// own role values without changing this type.
/// </summary>
/// <remarks>
/// Role names are normalized to trimmed, lowercase, ordinal form at construction. An
/// uninitialized (<see langword="default" />) instance equals <see cref="Unknown" /> and
/// renders as <c>"unknown"</c> — its <see cref="Name" /> is never null.
/// </remarks>
public readonly struct ProtocolRole : IEquatable<ProtocolRole>
{
    private const string UnknownName = "unknown";

    private readonly string? _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolRole" /> struct.
    /// </summary>
    /// <param name="name">The role name. Normalized to trimmed lowercase.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name" /> is null or whitespace.</exception>
    public ProtocolRole(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the role representing an unknown or unspecified party role.
    /// </summary>
    public static ProtocolRole Unknown => default;

    /// <summary>
    /// Gets the identity provider role (an OpenID Connect provider / SAML identity
    /// provider — the party that authenticates subjects and issues assertions).
    /// </summary>
    public static ProtocolRole IdentityProvider { get; } = new("identity-provider");

    /// <summary>
    /// Gets the relying party role (an OpenID Connect relying party / SAML service
    /// provider — the party that consumes assertions).
    /// </summary>
    public static ProtocolRole RelyingParty { get; } = new("relying-party");

    /// <summary>
    /// Gets the authorization server role (OAuth 2.0).
    /// </summary>
    public static ProtocolRole AuthorizationServer { get; } = new("authorization-server");

    /// <summary>
    /// Gets the resource server role (OAuth 2.0).
    /// </summary>
    public static ProtocolRole ResourceServer { get; } = new("resource-server");

    /// <summary>
    /// Gets the issuer role — the party asserted as the origin of a message, token, or
    /// assertion.
    /// </summary>
    public static ProtocolRole Issuer { get; } = new("issuer");

    /// <summary>
    /// Gets the audience role — the party an assertion or token is intended for.
    /// </summary>
    public static ProtocolRole Audience { get; } = new("audience");

    /// <summary>
    /// Gets the normalized role name. Never null; the unknown role renders as
    /// <c>"unknown"</c>.
    /// </summary>
    public string Name => _name ?? UnknownName;

    /// <inheritdoc />
    public bool Equals(ProtocolRole other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolRole other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>
    /// Determines whether two roles are equal.
    /// </summary>
    /// <param name="left">The first role.</param>
    /// <param name="right">The second role.</param>
    /// <returns><see langword="true" /> when the roles are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(ProtocolRole left, ProtocolRole right) => left.Equals(right);

    /// <summary>
    /// Determines whether two roles are unequal.
    /// </summary>
    /// <param name="left">The first role.</param>
    /// <param name="right">The second role.</param>
    /// <returns><see langword="true" /> when the roles are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(ProtocolRole left, ProtocolRole right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name;
}
