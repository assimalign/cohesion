using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Describes the contents of a SAML <c>AuthnRequest</c> before it is materialized into an
/// immutable <see cref="SamlAuthnRequest" />.
/// </summary>
public class SamlAuthnRequestDescriptor : ProtocolRequestDescriptor
{
    /// <summary>
    /// Gets or sets the assertion consumer service URL the response should be sent to. A
    /// request sets this or <see cref="AssertionConsumerServiceIndex" />, not both.
    /// </summary>
    public string? AssertionConsumerServiceUrl { get; set; }

    /// <summary>
    /// Gets or sets the indexed assertion consumer service the response should be sent to.
    /// </summary>
    public int? AssertionConsumerServiceIndex { get; set; }

    /// <summary>
    /// Gets or sets the requested response binding URI (SAML <c>ProtocolBinding</c>).
    /// </summary>
    public string? ResponseBinding { get; set; }

    /// <summary>
    /// Gets or sets whether the identity provider must re-authenticate the user
    /// (<c>ForceAuthn</c>).
    /// </summary>
    public bool? ForceAuthn { get; set; }

    /// <summary>
    /// Gets or sets whether the identity provider must not take control of the user
    /// interface (<c>IsPassive</c>).
    /// </summary>
    public bool? IsPassive { get; set; }

    /// <summary>
    /// Gets or sets the requested NameID format (<c>NameIDPolicy/@Format</c>).
    /// </summary>
    public string? NameIdPolicyFormat { get; set; }

    /// <summary>
    /// Gets or sets whether the identity provider may create a new identifier
    /// (<c>NameIDPolicy/@AllowCreate</c>).
    /// </summary>
    public bool? NameIdPolicyAllowCreate { get; set; }

    /// <summary>
    /// Gets or sets the requested service-provider name qualifier
    /// (<c>NameIDPolicy/@SPNameQualifier</c>).
    /// </summary>
    public string? NameIdPolicySpNameQualifier { get; set; }

    /// <summary>
    /// Gets the requested authentication context class references.
    /// </summary>
    public IList<string> RequestedAuthnContextClassRefs { get; } = new List<string>();

    /// <summary>
    /// Gets the requested authentication context declaration references.
    /// </summary>
    public IList<string> RequestedAuthnContextDeclRefs { get; } = new List<string>();

    /// <summary>
    /// Gets or sets the requested authentication context comparison
    /// (<c>exact</c>/<c>minimum</c>/<c>maximum</c>/<c>better</c>). Null preserves an absent
    /// wire value (the default is <c>exact</c>, a consumer concern, not a materialization
    /// default).
    /// </summary>
    public string? RequestedAuthnContextComparison { get; set; }

    /// <summary>
    /// Gets or sets the human-readable requesting provider name (<c>ProviderName</c>).
    /// </summary>
    public string? ProviderName { get; set; }

    /// <summary>
    /// Gets or sets the consent identifier (<c>Consent</c>).
    /// </summary>
    public string? Consent { get; set; }
}
