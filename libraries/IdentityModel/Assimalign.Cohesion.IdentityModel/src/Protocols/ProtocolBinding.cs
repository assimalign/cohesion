using System;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents how a protocol message travels over a transport, as an open, extensible
/// vocabulary of <em>transport shapes</em>. The vocabulary deliberately contains one name
/// per wire shape: protocol-specific spellings (SAML binding URIs, OpenID Connect
/// <c>response_mode</c> values) are branch-owned constants that map onto these values, so
/// stored metadata and routing logic always compare one canonical spelling.
/// </summary>
/// <remarks>
/// A binding names the transport shape only; the message encoding on that transport (for
/// example SAML's DEFLATE-plus-signature query encoding versus plain OAuth query
/// parameters) is determined by the accompanying protocol, so dispatch is always on the
/// (protocol, binding) pair. Binding names are normalized to trimmed, lowercase, ordinal
/// form at construction; the <see langword="default" /> instance equals
/// <see cref="Unknown" /> and its <see cref="Name" /> is never null.
/// </remarks>
public readonly struct ProtocolBinding : IEquatable<ProtocolBinding>
{
    private const string UnknownName = "unknown";

    private readonly string? _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolBinding" /> struct.
    /// </summary>
    /// <param name="name">The binding name. Normalized to trimmed lowercase.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="name" /> is null or whitespace.</exception>
    public ProtocolBinding(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Gets the binding representing an unknown or unspecified transport shape.
    /// </summary>
    public static ProtocolBinding Unknown => default;

    /// <summary>
    /// Gets the query-carrying HTTP redirect shape (SAML HTTP-Redirect; OpenID Connect
    /// <c>response_mode=query</c>).
    /// </summary>
    public static ProtocolBinding HttpRedirect { get; } = new("http-redirect");

    /// <summary>
    /// Gets the auto-submitted HTML form POST shape (SAML HTTP-POST; OpenID Connect
    /// <c>response_mode=form_post</c>).
    /// </summary>
    public static ProtocolBinding HttpPost { get; } = new("http-post");

    /// <summary>
    /// Gets the fragment-carrying HTTP redirect shape (OpenID Connect
    /// <c>response_mode=fragment</c>). SAML does not use this shape; a neutral vocabulary
    /// lists every wire shape, not only the shared ones.
    /// </summary>
    public static ProtocolBinding HttpFragment { get; } = new("http-fragment");

    /// <summary>
    /// Gets the artifact shape: a small reference travels the front channel and the real
    /// message is resolved over a back channel (SAML HTTP-Artifact).
    /// </summary>
    public static ProtocolBinding HttpArtifact { get; } = new("http-artifact");

    /// <summary>
    /// Gets the SOAP shape (SAML SOAP binding, for example artifact resolution and SOAP
    /// single logout).
    /// </summary>
    public static ProtocolBinding Soap { get; } = new("soap");

    /// <summary>
    /// Gets the direct server-to-server shape with the response returning on the same
    /// connection (OAuth token endpoint, OpenID Connect back-channel logout).
    /// </summary>
    public static ProtocolBinding BackChannel { get; } = new("back-channel");

    /// <summary>
    /// Gets the normalized binding name. Never null; the unknown binding renders as
    /// <c>"unknown"</c>.
    /// </summary>
    public string Name => _name ?? UnknownName;

    /// <inheritdoc />
    public bool Equals(ProtocolBinding other) => string.Equals(Name, other.Name, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ProtocolBinding other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Name);

    /// <summary>
    /// Determines whether two bindings are equal.
    /// </summary>
    /// <param name="left">The first binding.</param>
    /// <param name="right">The second binding.</param>
    /// <returns><see langword="true" /> when the bindings are equal; otherwise <see langword="false" />.</returns>
    public static bool operator ==(ProtocolBinding left, ProtocolBinding right) => left.Equals(right);

    /// <summary>
    /// Determines whether two bindings are unequal.
    /// </summary>
    /// <param name="left">The first binding.</param>
    /// <param name="right">The second binding.</param>
    /// <returns><see langword="true" /> when the bindings are unequal; otherwise <see langword="false" />.</returns>
    public static bool operator !=(ProtocolBinding left, ProtocolBinding right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => Name;
}
