using System;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents an authentication protocol as an open, extensible vocabulary. Well-known
/// protocols are exposed as static members; additional protocols can be introduced without
/// changing this type.
/// </summary>
/// <remarks>
/// Protocol names are normalized to trimmed, lowercase, ordinal form at construction, so
/// <c>new AuthenticationProtocol("SAML2")</c> equals <see cref="Saml2" />. An uninitialized
/// (<see langword="default" />) instance equals <see cref="Unknown" /> and renders as
/// <c>"unknown"</c> — its <see cref="Name" /> is never null.
/// </remarks>
public readonly struct AuthenticationProtocol : IEquatable<AuthenticationProtocol>
{
    private const string UnknownName = "unknown";

    private readonly string? _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationProtocol" /> struct.
    /// </summary>
    /// <param name="name">The protocol name. Normalized to trimmed lowercase.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name" /> is null or whitespace.</exception>
    public AuthenticationProtocol(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the protocol representing an unknown or unspecified provenance.
    /// </summary>
    public static AuthenticationProtocol Unknown => default;

    /// <summary>
    /// Gets the OpenID Connect protocol.
    /// </summary>
    public static AuthenticationProtocol OpenIdConnect { get; } = new("oidc");

    /// <summary>
    /// Gets the OAuth 2.0 protocol, for authentication events that are OAuth-only
    /// (for example client-credentials grants) rather than OpenID Connect.
    /// </summary>
    public static AuthenticationProtocol OAuth2 { get; } = new("oauth2");

    /// <summary>
    /// Gets the SAML 2.0 protocol.
    /// </summary>
    public static AuthenticationProtocol Saml2 { get; } = new("saml2");

    /// <summary>
    /// Gets the normalized protocol name. Never null; the unknown protocol renders as
    /// <c>"unknown"</c>.
    /// </summary>
    public string Name => _name ?? UnknownName;

    /// <inheritdoc />
    public bool Equals(AuthenticationProtocol other)
        => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is AuthenticationProtocol other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>
    /// Determines whether two protocols are equal.
    /// </summary>
    /// <param name="left">The first protocol.</param>
    /// <param name="right">The second protocol.</param>
    /// <returns><see langword="true" /> when the protocols are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(AuthenticationProtocol left, AuthenticationProtocol right) => left.Equals(right);

    /// <summary>
    /// Determines whether two protocols are unequal.
    /// </summary>
    /// <param name="left">The first protocol.</param>
    /// <param name="right">The second protocol.</param>
    /// <returns><see langword="true" /> when the protocols are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(AuthenticationProtocol left, AuthenticationProtocol right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name;
}
