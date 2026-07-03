using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents an immutable description of a protocol endpoint: where and how messages of a
/// given kind are exchanged with a party. Endpoints are descriptive — this library never
/// connects to them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Location" /> and <see cref="ResponseLocation" /> are stored as wire-exact
/// strings (validated to be absolute URIs at materialization) and must be compared
/// ordinally: endpoint and destination comparison is a signed-value security control in
/// SAML and an exact-match rule for OAuth redirect URIs, and <see cref="Uri" />
/// normalization (default-port dropping, host case folding) would silently weaken it.
/// The type deliberately has no equality semantics — matching policy (exact string,
/// loopback-port-insensitive, and so on) belongs to protocol branches and validators.
/// </para>
/// <para>
/// The SAML default-endpoint selection rule depends on the tri-state
/// <see cref="IsDefault" />: the default endpoint among a kind is the <em>first</em> one
/// explicitly marked <see langword="true" />; otherwise the first endpoint not explicitly
/// marked <see langword="false" /> (that is, the first with a null
/// <see cref="IsDefault" />); otherwise the first endpoint in document order. Null
/// preserves "the document said nothing".
/// </para>
/// </remarks>
public sealed class ProtocolEndpoint
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolEndpoint" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The endpoint contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no location, or when a location is not an absolute
    /// URI.
    /// </exception>
    public ProtocolEndpoint(ProtocolEndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.Location))
        {
            throw new IdentityModelException("A protocol endpoint requires a location.");
        }

        if (!IsExplicitAbsoluteUri(descriptor.Location))
        {
            throw new IdentityModelException(
                $"The endpoint location '{descriptor.Location}' is not an absolute URI.");
        }

        if (descriptor.ResponseLocation is not null &&
            !IsExplicitAbsoluteUri(descriptor.ResponseLocation))
        {
            throw new IdentityModelException(
                $"The endpoint response location '{descriptor.ResponseLocation}' is not an absolute URI.");
        }

        Kind = descriptor.Kind;
        Location = descriptor.Location;
        ResponseLocation = descriptor.ResponseLocation;
        Binding = descriptor.Binding;
        Role = descriptor.Role;
        Index = descriptor.Index;
        IsDefault = descriptor.IsDefault;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets what the endpoint is for.
    /// </summary>
    public ProtocolEndpointKind Kind { get; }

    /// <summary>
    /// Gets the endpoint location as the exact wire string. Compare ordinally.
    /// </summary>
    public string Location { get; }

    /// <summary>
    /// Gets the location responses are returned to, when it differs from
    /// <see cref="Location" /> (SAML <c>ResponseLocation</c>).
    /// </summary>
    public string? ResponseLocation { get; }

    /// <summary>
    /// Gets the transport shape messages use at this endpoint.
    /// </summary>
    public ProtocolBinding Binding { get; }

    /// <summary>
    /// Gets the role this endpoint serves, when the publishing entity plays several roles.
    /// Null means entity-wide.
    /// </summary>
    public ProtocolRole? Role { get; }

    /// <summary>
    /// Gets the endpoint index (SAML indexed endpoints).
    /// </summary>
    public int? Index { get; }

    /// <summary>
    /// Gets whether this endpoint is explicitly the default among endpoints of its kind;
    /// null when the source document did not say.
    /// </summary>
    public bool? IsDefault { get; }

    /// <summary>
    /// Gets additional endpoint detail.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind} {Location} ({Binding})";

    private static bool IsExplicitAbsoluteUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // System.Uri accepts implicit file paths ("C:\...", "\\server\share"),
        // scheme-relative strings ("//host/path"), and whitespace-padded input as
        // "absolute" — and does so differently per OS ("/path" is an absolute file URI on
        // Unix only). Requiring the wire string to spell its scheme explicitly rejects
        // all of those identically on every platform while still accepting private-use
        // schemes such as "com.example.app:/cb".
        return value.Length > uri.Scheme.Length
            && value.StartsWith(uri.Scheme, StringComparison.OrdinalIgnoreCase)
            && value[uri.Scheme.Length] == ':';
    }
}
