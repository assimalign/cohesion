using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Describes the contents of a protocol key before it is materialized into an immutable
/// <see cref="ProtocolKey" />.
/// </summary>
public class ProtocolKeyDescriptor
{
    /// <summary>
    /// Gets or sets the declared use restriction. Defaults to
    /// <see cref="ProtocolKeyUse.Unspecified" />, which means no restriction was declared.
    /// </summary>
    public ProtocolKeyUse Use { get; set; }

    /// <summary>
    /// Gets or sets the key identifier (JWK <c>kid</c> / a key name).
    /// </summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// Gets or sets the role this key serves, when the publishing entity plays several
    /// roles (SAML role descriptors each carry their own keys). Null means entity-wide.
    /// </summary>
    public ProtocolRole? Role { get; set; }

    /// <summary>
    /// Gets the certificates carrying the key, as base64 DER strings (SAML
    /// <c>X509Certificate</c> content / JWK <c>x5c</c> entries), in order.
    /// </summary>
    public IList<string> Certificates { get; } = new List<string>();

    /// <summary>
    /// Gets the algorithms the key is declared for (SAML <c>EncryptionMethod</c>
    /// algorithm URIs / JWK <c>alg</c>), in order. Empty means undeclared.
    /// </summary>
    public IList<string> Algorithms { get; } = new List<string>();

    /// <summary>
    /// Gets additional key detail (for example JWK members such as <c>n</c>/<c>e</c> or
    /// SAML <c>EncryptionMethod</c> attributes) as typed values.
    /// </summary>
    public IDictionary<string, IdentityClaimValue> Properties { get; } =
        new Dictionary<string, IdentityClaimValue>(StringComparer.Ordinal);
}
