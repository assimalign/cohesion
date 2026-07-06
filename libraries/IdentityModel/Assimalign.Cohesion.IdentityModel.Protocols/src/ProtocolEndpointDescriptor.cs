using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the contents of a protocol endpoint before it is materialized into an
/// immutable <see cref="ProtocolEndpoint" />.
/// </summary>
public class ProtocolEndpointDescriptor
{
    /// <summary>
    /// Gets or sets what the endpoint is for.
    /// </summary>
    public ProtocolEndpointKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the endpoint location. Required at materialization and must parse as
    /// an absolute URI; the exact string is preserved for wire-exact comparison.
    /// </summary>
    public string? Location { get; set; }

    /// <summary>
    /// Gets or sets the location responses are returned to, when it differs from
    /// <see cref="Location" /> (SAML <c>ResponseLocation</c>). Must parse as an absolute
    /// URI when set.
    /// </summary>
    public string? ResponseLocation { get; set; }

    /// <summary>
    /// Gets or sets the transport shape messages use at this endpoint.
    /// </summary>
    public ProtocolBinding Binding { get; set; }

    /// <summary>
    /// Gets or sets the role this endpoint serves, when the publishing entity plays
    /// several roles (SAML role descriptors). Null means entity-wide.
    /// </summary>
    public ProtocolRole? Role { get; set; }

    /// <summary>
    /// Gets or sets the endpoint index (SAML indexed endpoints).
    /// </summary>
    public int? Index { get; set; }

    /// <summary>
    /// Gets or sets whether this endpoint is the default among endpoints of its kind.
    /// Null means the wire document did not state a preference, which SAML's
    /// default-endpoint selection treats differently from an explicit
    /// <see langword="false" />.
    /// </summary>
    public bool? IsDefault { get; set; }

    /// <summary>
    /// Gets additional endpoint detail.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
