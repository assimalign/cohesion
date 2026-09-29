using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Represents the shared shape of <em>published entity metadata</em>: what a known,
/// identified party declares about itself (an OpenID Connect discovery document, a SAML
/// <c>EntityDescriptor</c>, a registered-client record). Protocol branches derive concrete
/// metadata types that add their spec surface on top of this shared core.
/// </summary>
/// <remarks>
/// <para>
/// The base is data-only by design: get-only properties snapshotted from a descriptor,
/// no virtual or abstract behavior members, so deriving types can never change base
/// semantics. The protocol is pinned by the derived type — it is a constructor argument
/// supplied by the derivative, not descriptor data — so a metadata object can never claim
/// a protocol that contradicts its type.
/// </para>
/// <para>
/// <see cref="EntityId" /> is always required: this type models metadata about an
/// <em>existing, identified</em> entity. Shapes that have no identifier yet — for example
/// an OpenID Connect dynamic client registration <em>request</em>, where the server
/// assigns <c>client_id</c> in the response — are branch-owned request types and do not
/// derive from this base.
/// </para>
/// <para>
/// Endpoints and keys are flat lists; when the publishing entity plays several roles (a
/// SAML entity that is both identity provider and service provider), each endpoint and key
/// carries its role scope on <see cref="ProtocolEndpoint.Role" /> /
/// <see cref="ProtocolKey.Role" />. Role-grouped projections are a branch concern; the
/// flat lists remain the single source of truth. <see cref="CacheDuration" /> is a
/// computed value: SAML <c>xs:duration</c> calendar components (for example <c>P1M</c>)
/// have no exact <see cref="TimeSpan" /> representation, so branches preserve the original
/// lexical form in <see cref="Properties" /> when the conversion is inexact.
/// </para>
/// </remarks>
public abstract class ProtocolMetadata
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProtocolMetadata" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The shared metadata contents.</param>
    /// <param name="protocol">The protocol the derived metadata type models. Supplied by the derivative, never by descriptor data.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a property name is blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no entity identifier or contains null endpoint or
    /// key entries.
    /// </exception>
    protected ProtocolMetadata(ProtocolMetadataDescriptor descriptor, AuthenticationProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (string.IsNullOrWhiteSpace(descriptor.EntityId))
        {
            throw new IdentityModelException("Protocol metadata requires an entity identifier.");
        }

        Protocol = protocol;
        EntityId = descriptor.EntityId;
        Roles = SnapshotRoles(descriptor.Roles);
        Endpoints = SnapshotList(descriptor.Endpoints);
        Keys = SnapshotList(descriptor.Keys);
        ValidUntil = descriptor.ValidUntil;
        CacheDuration = descriptor.CacheDuration;
        RawDocument = descriptor.RawDocument;
        Properties = ModelSnapshot.Properties(descriptor.Properties, nameof(descriptor));
    }

    /// <summary>
    /// Gets the protocol the metadata belongs to.
    /// </summary>
    public AuthenticationProtocol Protocol { get; }

    /// <summary>
    /// Gets the entity identifier (an OpenID Connect issuer URL / a SAML <c>entityID</c>),
    /// as the exact wire string.
    /// </summary>
    public string EntityId { get; }

    /// <summary>
    /// Gets the roles the entity plays.
    /// </summary>
    public IReadOnlyList<ProtocolRole> Roles { get; }

    /// <summary>
    /// Gets the endpoints the entity publishes. Entries carry their role scope when the
    /// entity plays several roles.
    /// </summary>
    public IReadOnlyList<ProtocolEndpoint> Endpoints { get; }

    /// <summary>
    /// Gets the keys the entity publishes. Entries carry their role scope when the entity
    /// plays several roles.
    /// </summary>
    public IReadOnlyList<ProtocolKey> Keys { get; }

    /// <summary>
    /// Gets the instant the metadata expires (SAML <c>validUntil</c>).
    /// </summary>
    public DateTimeOffset? ValidUntil { get; }

    /// <summary>
    /// Gets how long the metadata may be cached (SAML <c>cacheDuration</c>). See the type
    /// remarks for the <c>xs:duration</c> fidelity caveat.
    /// </summary>
    public TimeSpan? CacheDuration { get; }

    /// <summary>
    /// Gets the as-received document text the metadata was materialized from (metadata
    /// XML, discovery JSON, or a signed-metadata JWT), preserved so signatures can be
    /// re-verified later. Null when not retained.
    /// </summary>
    public string? RawDocument { get; }

    /// <summary>
    /// Gets additional metadata detail.
    /// </summary>
    public IReadOnlyDictionary<string, IdentityClaimValue> Properties { get; }

    /// <inheritdoc />
    public override string ToString() => $"{EntityId} ({Protocol})";

    private static IReadOnlyList<ProtocolRole> SnapshotRoles(IList<ProtocolRole> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
        {
            return Array.Empty<ProtocolRole>();
        }

        var snapshot = new ProtocolRole[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index];
        }

        return new ReadOnlyCollection<ProtocolRole>(snapshot);
    }

    private static IReadOnlyList<TItem> SnapshotList<TItem>(IList<TItem> source)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Count == 0)
        {
            return Array.Empty<TItem>();
        }

        var snapshot = new TItem[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = source[index]
                ?? throw new IdentityModelException("Protocol metadata lists must not contain null entries.");
        }

        return new ReadOnlyCollection<TItem>(snapshot);
    }
}
