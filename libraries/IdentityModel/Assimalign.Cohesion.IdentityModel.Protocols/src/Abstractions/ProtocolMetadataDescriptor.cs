using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the shared contents of published entity metadata before it is materialized
/// into a <see cref="ProtocolMetadata" /> derivative. Abstract because
/// <see cref="ProtocolMetadata" /> is abstract: protocol branches pair each concrete
/// metadata type with its own descriptor derivative.
/// </summary>
public abstract class ProtocolMetadataDescriptor
{
    /// <summary>
    /// Gets or sets the entity identifier (an OpenID Connect issuer URL / a SAML
    /// <c>entityID</c>). Required at materialization.
    /// </summary>
    public string? EntityId { get; set; }

    /// <summary>
    /// Gets the roles the entity plays.
    /// </summary>
    public IList<ProtocolRole> Roles { get; } = new List<ProtocolRole>();

    /// <summary>
    /// Gets the endpoints the entity publishes.
    /// </summary>
    public IList<ProtocolEndpoint> Endpoints { get; } = new List<ProtocolEndpoint>();

    /// <summary>
    /// Gets the keys the entity publishes.
    /// </summary>
    public IList<ProtocolKey> Keys { get; } = new List<ProtocolKey>();

    /// <summary>
    /// Gets or sets the instant the metadata expires (SAML <c>validUntil</c>).
    /// </summary>
    public DateTimeOffset? ValidUntil { get; set; }

    /// <summary>
    /// Gets or sets how long the metadata may be cached (SAML <c>cacheDuration</c>).
    /// </summary>
    public TimeSpan? CacheDuration { get; set; }

    /// <summary>
    /// Gets or sets the as-received document text the metadata was materialized from.
    /// </summary>
    public string? RawDocument { get; set; }

    /// <summary>
    /// Gets additional metadata detail.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
