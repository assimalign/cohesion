using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 metadata role descriptor (an <c>IDPSSODescriptor</c> or
/// <c>SPSSODescriptor</c>): the endpoints, keys, supported NameID formats, and signing
/// policy for one role a SAML entity plays. This is the authoritative per-role view; the
/// enclosing <see cref="SamlEntityMetadata" />'s flat endpoint and key lists are the
/// projection, with each entry scoped to its role.
/// </summary>
public sealed class SamlRoleDescriptor
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlRoleDescriptor" /> class.
    /// </summary>
    /// <param name="role">The role this descriptor describes (identity provider or relying party).</param>
    /// <param name="endpoints">The role's endpoints. Each should carry <see cref="ProtocolEndpoint.Role" /> equal to <paramref name="role" />.</param>
    /// <param name="keys">The role's keys. Each should carry <see cref="ProtocolKey.Role" /> equal to <paramref name="role" />.</param>
    /// <param name="nameIdFormats">The NameID formats the role supports.</param>
    /// <param name="protocolSupportEnumeration">The protocol support enumeration (a space-delimited set of protocol namespace URIs).</param>
    /// <param name="wantAuthnRequestsSigned">Whether an identity-provider role wants signed authentication requests.</param>
    /// <param name="authnRequestsSigned">Whether a service-provider role signs its authentication requests.</param>
    /// <param name="wantAssertionsSigned">Whether a service-provider role wants signed assertions.</param>
    /// <param name="validUntil">The instant the role descriptor expires.</param>
    /// <exception cref="ArgumentNullException">Thrown when an endpoint or key entry is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a NameID format entry is null or whitespace.</exception>
    public SamlRoleDescriptor(
        ProtocolRole role,
        IEnumerable<ProtocolEndpoint>? endpoints = null,
        IEnumerable<ProtocolKey>? keys = null,
        IEnumerable<string>? nameIdFormats = null,
        string? protocolSupportEnumeration = null,
        bool? wantAuthnRequestsSigned = null,
        bool? authnRequestsSigned = null,
        bool? wantAssertionsSigned = null,
        DateTimeOffset? validUntil = null)
    {
        Role = role;
        ProtocolSupportEnumeration = protocolSupportEnumeration;
        WantAuthnRequestsSigned = wantAuthnRequestsSigned;
        AuthnRequestsSigned = authnRequestsSigned;
        WantAssertionsSigned = wantAssertionsSigned;
        ValidUntil = validUntil;
        Endpoints = SnapshotRefs(endpoints, nameof(endpoints));
        Keys = SnapshotRefs(keys, nameof(keys));
        NameIdFormats = SnapshotStrings(nameIdFormats, nameof(nameIdFormats));
    }

    /// <summary>
    /// Gets the role this descriptor describes.
    /// </summary>
    public ProtocolRole Role { get; }

    /// <summary>
    /// Gets the role's endpoints.
    /// </summary>
    public IReadOnlyList<ProtocolEndpoint> Endpoints { get; }

    /// <summary>
    /// Gets the role's keys.
    /// </summary>
    public IReadOnlyList<ProtocolKey> Keys { get; }

    /// <summary>
    /// Gets the NameID formats the role supports.
    /// </summary>
    public IReadOnlyList<string> NameIdFormats { get; }

    /// <summary>
    /// Gets the protocol support enumeration.
    /// </summary>
    public string? ProtocolSupportEnumeration { get; }

    /// <summary>
    /// Gets whether an identity-provider role wants signed authentication requests.
    /// </summary>
    public bool? WantAuthnRequestsSigned { get; }

    /// <summary>
    /// Gets whether a service-provider role signs its authentication requests.
    /// </summary>
    public bool? AuthnRequestsSigned { get; }

    /// <summary>
    /// Gets whether a service-provider role wants signed assertions.
    /// </summary>
    public bool? WantAssertionsSigned { get; }

    /// <summary>
    /// Gets the instant the role descriptor expires.
    /// </summary>
    public DateTimeOffset? ValidUntil { get; }

    private static IReadOnlyList<TItem> SnapshotRefs<TItem>(IEnumerable<TItem>? source, string parameterName)
        where TItem : class
    {
        if (source is null)
        {
            return Array.Empty<TItem>();
        }

        var snapshot = new List<TItem>();
        foreach (var item in source)
        {
            ArgumentNullException.ThrowIfNull(item, parameterName);
            snapshot.Add(item);
        }

        return new ReadOnlyCollection<TItem>(snapshot.ToArray());
    }

    private static IReadOnlyList<string> SnapshotStrings(IEnumerable<string>? source, string parameterName)
    {
        if (source is null)
        {
            return Array.Empty<string>();
        }

        var snapshot = new List<string>();
        foreach (var item in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item, parameterName);
            snapshot.Add(item);
        }

        return new ReadOnlyCollection<string>(snapshot.ToArray());
    }
}
