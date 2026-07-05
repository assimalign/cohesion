namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML-minted validation diagnostic codes. Cross-protocol concepts (issuer
/// mismatch, audience mismatch, missing required members, expiry) use the shared
/// <see cref="ProtocolValidationCodes" />; this class carries only diagnostics SAML 2.0
/// itself defines.
/// </summary>
public static class SamlValidationCodes
{
    /// <summary>A response carries no assertion where one is required.</summary>
    public const string AssertionMissing = "assertion_missing";

    /// <summary>An assertion carries no subject.</summary>
    public const string SubjectMissing = "subject_missing";

    /// <summary>No subject confirmation satisfies the bearer confirmation rules.</summary>
    public const string SubjectConfirmationInvalid = "subject_confirmation_invalid";

    /// <summary>An assertion's audience restriction is not satisfied.</summary>
    public const string AudienceRestrictionFailed = "audience_restriction_failed";

    /// <summary>An authentication statement carries no authentication context.</summary>
    public const string AuthnContextMissing = "authn_context_missing";

    /// <summary>A response or logout response reports a non-success status.</summary>
    public const string StatusNotSuccess = "status_not_success";

    /// <summary>A response's <c>InResponseTo</c> does not match the sent request.</summary>
    public const string InResponseToMismatch = "in_response_to_mismatch";

    /// <summary>A message's destination does not match the receiving endpoint.</summary>
    public const string DestinationMismatch = "destination_mismatch";

    /// <summary>A metadata entity publishes no role descriptor.</summary>
    public const string RoleDescriptorMissing = "role_descriptor_missing";

    /// <summary>A role-scoped key or endpoint is not attributed to its role.</summary>
    public const string RoleScopeMissing = "role_scope_missing";

    /// <summary>An indexed endpoint lacks its required index, or a plain endpoint carries one.</summary>
    public const string EndpointIndexInvalid = "endpoint_index_invalid";

    /// <summary>An authentication request sets conflicting or invalid parameters.</summary>
    public const string RequestParametersInvalid = "request_parameters_invalid";
}
