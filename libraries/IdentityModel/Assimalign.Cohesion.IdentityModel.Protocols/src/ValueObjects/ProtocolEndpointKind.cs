using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents what a protocol endpoint is for, as an open, extensible vocabulary. The root
/// library ships only the vocabulary type; well-known endpoint kinds are protocol-spec
/// vocabulary and live as typed values in the owning protocol branch (for example the
/// OpenID Connect branch's authorization/token/UserInfo kinds and the SAML branch's
/// single-sign-on/assertion-consumer kinds).
/// </summary>
/// <remarks>
/// Kind names are normalized to trimmed, lowercase, ordinal form at construction so that
/// endpoint lookup by kind is normalization-path independent. The <see langword="default" />
/// instance equals <see cref="Unknown" /> and its <see cref="Name" /> is never null.
/// </remarks>
public readonly struct ProtocolEndpointKind : IEquatable<ProtocolEndpointKind>
{
    private const string UnknownName = "unknown";

    private readonly string? _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolEndpointKind" /> struct.
    /// </summary>
    /// <param name="name">The kind name. Normalized to trimmed lowercase.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name" /> is null or whitespace.</exception>
    public ProtocolEndpointKind(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the kind representing an unknown or unspecified endpoint purpose.
    /// </summary>
    public static ProtocolEndpointKind Unknown => default;

    /// <summary>
    /// Gets the normalized kind name. Never null; the unknown kind renders as
    /// <c>"unknown"</c>.
    /// </summary>
    public string Name => _name ?? UnknownName;

    /// <inheritdoc />
    public bool Equals(ProtocolEndpointKind other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolEndpointKind other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>
    /// Determines whether two endpoint kinds are equal.
    /// </summary>
    /// <param name="left">The first kind.</param>
    /// <param name="right">The second kind.</param>
    /// <returns><see langword="true" /> when the kinds are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(ProtocolEndpointKind left, ProtocolEndpointKind right) => left.Equals(right);

    /// <summary>
    /// Determines whether two endpoint kinds are unequal.
    /// </summary>
    /// <param name="left">The first kind.</param>
    /// <param name="right">The second kind.</param>
    /// <returns><see langword="true" /> when the kinds are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(ProtocolEndpointKind left, ProtocolEndpointKind right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name;
}
