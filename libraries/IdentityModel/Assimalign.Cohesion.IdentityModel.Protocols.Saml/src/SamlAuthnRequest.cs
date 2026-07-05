using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>AuthnRequest</c> (SAML Core §3.4.1). The base envelope carries
/// the message identifier (<c>ID</c>), the issuer (the service provider entity id), the
/// destination, the issue instant, and the relay state (on
/// <see cref="ProtocolMessage.CorrelationState" />).
/// </summary>
public sealed class SamlAuthnRequest : ProtocolRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlAuthnRequest" /> class by
    /// snapshotting the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The request contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a requested-context entry is null or whitespace, or a property name is
    /// blank or a property value is undefined.
    /// </exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when the descriptor has no message identifier — the member that makes the
    /// artifact recognizable as a SAML request.
    /// </exception>
    public SamlAuthnRequest(SamlAuthnRequestDescriptor descriptor)
        : base(descriptor, AuthenticationProtocol.Saml2)
    {
        if (string.IsNullOrWhiteSpace(descriptor.MessageId))
        {
            throw new IdentityModelException("A SAML authentication request requires a message identifier.");
        }

        AssertionConsumerServiceUrl = descriptor.AssertionConsumerServiceUrl;
        AssertionConsumerServiceIndex = descriptor.AssertionConsumerServiceIndex;
        ResponseBinding = descriptor.ResponseBinding;
        ForceAuthn = descriptor.ForceAuthn;
        IsPassive = descriptor.IsPassive;
        NameIdPolicyFormat = descriptor.NameIdPolicyFormat;
        NameIdPolicyAllowCreate = descriptor.NameIdPolicyAllowCreate;
        NameIdPolicySpNameQualifier = descriptor.NameIdPolicySpNameQualifier;
        RequestedAuthnContextClassRefs = ModelSnapshot.Strings(descriptor.RequestedAuthnContextClassRefs, nameof(descriptor));
        RequestedAuthnContextDeclRefs = ModelSnapshot.Strings(descriptor.RequestedAuthnContextDeclRefs, nameof(descriptor));
        RequestedAuthnContextComparison = descriptor.RequestedAuthnContextComparison;
        ProviderName = descriptor.ProviderName;
        Consent = descriptor.Consent;
    }

    /// <summary>
    /// Gets the assertion consumer service URL the response should be sent to.
    /// </summary>
    public string? AssertionConsumerServiceUrl { get; }

    /// <summary>
    /// Gets the indexed assertion consumer service the response should be sent to.
    /// </summary>
    public int? AssertionConsumerServiceIndex { get; }

    /// <summary>
    /// Gets the requested response binding URI.
    /// </summary>
    public string? ResponseBinding { get; }

    /// <summary>
    /// Gets whether the identity provider must re-authenticate the user.
    /// </summary>
    public bool? ForceAuthn { get; }

    /// <summary>
    /// Gets whether the identity provider must not take control of the user interface.
    /// </summary>
    public bool? IsPassive { get; }

    /// <summary>
    /// Gets the requested NameID format.
    /// </summary>
    public string? NameIdPolicyFormat { get; }

    /// <summary>
    /// Gets whether the identity provider may create a new identifier.
    /// </summary>
    public bool? NameIdPolicyAllowCreate { get; }

    /// <summary>
    /// Gets the requested service-provider name qualifier.
    /// </summary>
    public string? NameIdPolicySpNameQualifier { get; }

    /// <summary>
    /// Gets the requested authentication context class references.
    /// </summary>
    public IReadOnlyList<string> RequestedAuthnContextClassRefs { get; }

    /// <summary>
    /// Gets the requested authentication context declaration references.
    /// </summary>
    public IReadOnlyList<string> RequestedAuthnContextDeclRefs { get; }

    /// <summary>
    /// Gets the requested authentication context comparison; null when the wire omitted it.
    /// </summary>
    public string? RequestedAuthnContextComparison { get; }

    /// <summary>
    /// Gets the human-readable requesting provider name.
    /// </summary>
    public string? ProviderName { get; }

    /// <summary>
    /// Gets the consent identifier.
    /// </summary>
    public string? Consent { get; }

    /// <summary>
    /// Validates the request's structural conformance: the assertion consumer service is
    /// identified by a URL or an index but not both, and <c>IsPassive</c> is not combined
    /// with <c>ForceAuthn</c>.
    /// </summary>
    /// <returns>The validation findings.</returns>
    public ProtocolValidationResult Validate()
    {
        var diagnostics = new List<ProtocolValidationDiagnostic>();

        if (AssertionConsumerServiceUrl is not null && AssertionConsumerServiceIndex is not null)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.RequestParametersInvalid,
                "An authentication request must not set both an assertion consumer service URL and index.",
                member: "AssertionConsumerService"));
        }

        if (ForceAuthn == true && IsPassive == true)
        {
            diagnostics.Add(new ProtocolValidationDiagnostic(
                ProtocolValidationSeverity.Error,
                SamlValidationCodes.RequestParametersInvalid,
                "An authentication request must not combine ForceAuthn with IsPassive.",
                member: "ForceAuthn"));
        }

        return diagnostics.Count == 0 ? ProtocolValidationResult.Success : new ProtocolValidationResult(diagnostics);
    }
}
