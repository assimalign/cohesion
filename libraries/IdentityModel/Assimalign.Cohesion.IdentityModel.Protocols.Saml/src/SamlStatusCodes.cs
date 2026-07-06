namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Defines the SAML 2.0 status code URIs (SAML Core §3.2.2.2), as the exact OASIS wire
/// strings consumed through <see cref="ProtocolResponseStatus.Code" /> (top-level) and
/// <see cref="ProtocolResponseStatus.SubCodes" /> (nested).
/// </summary>
public static class SamlStatusCodes
{
    /// <summary>The top-level success status.</summary>
    public const string Success = "urn:oasis:names:tc:SAML:2.0:status:Success";

    /// <summary>The top-level requester-error status.</summary>
    public const string Requester = "urn:oasis:names:tc:SAML:2.0:status:Requester";

    /// <summary>The top-level responder-error status.</summary>
    public const string Responder = "urn:oasis:names:tc:SAML:2.0:status:Responder";

    /// <summary>The top-level version-mismatch status.</summary>
    public const string VersionMismatch = "urn:oasis:names:tc:SAML:2.0:status:VersionMismatch";

    /// <summary>The second-level authentication-failed status.</summary>
    public const string AuthnFailed = "urn:oasis:names:tc:SAML:2.0:status:AuthnFailed";

    /// <summary>The second-level invalid-attribute status.</summary>
    public const string InvalidAttrNameOrValue = "urn:oasis:names:tc:SAML:2.0:status:InvalidAttrNameOrValue";

    /// <summary>The second-level invalid-NameID-policy status.</summary>
    public const string InvalidNameIdPolicy = "urn:oasis:names:tc:SAML:2.0:status:InvalidNameIDPolicy";

    /// <summary>The second-level no-authn-context status.</summary>
    public const string NoAuthnContext = "urn:oasis:names:tc:SAML:2.0:status:NoAuthnContext";

    /// <summary>The second-level no-available-IdP status.</summary>
    public const string NoAvailableIdp = "urn:oasis:names:tc:SAML:2.0:status:NoAvailableIDP";

    /// <summary>The second-level no-passive status.</summary>
    public const string NoPassive = "urn:oasis:names:tc:SAML:2.0:status:NoPassive";

    /// <summary>The second-level no-supported-IdP status.</summary>
    public const string NoSupportedIdp = "urn:oasis:names:tc:SAML:2.0:status:NoSupportedIDP";

    /// <summary>The second-level partial-logout status.</summary>
    public const string PartialLogout = "urn:oasis:names:tc:SAML:2.0:status:PartialLogout";

    /// <summary>The second-level proxy-count-exceeded status.</summary>
    public const string ProxyCountExceeded = "urn:oasis:names:tc:SAML:2.0:status:ProxyCountExceeded";

    /// <summary>The second-level request-denied status.</summary>
    public const string RequestDenied = "urn:oasis:names:tc:SAML:2.0:status:RequestDenied";

    /// <summary>The second-level request-unsupported status.</summary>
    public const string RequestUnsupported = "urn:oasis:names:tc:SAML:2.0:status:RequestUnsupported";

    /// <summary>The second-level request-version-deprecated status.</summary>
    public const string RequestVersionDeprecated = "urn:oasis:names:tc:SAML:2.0:status:RequestVersionDeprecated";

    /// <summary>The second-level request-version-too-high status.</summary>
    public const string RequestVersionTooHigh = "urn:oasis:names:tc:SAML:2.0:status:RequestVersionTooHigh";

    /// <summary>The second-level request-version-too-low status.</summary>
    public const string RequestVersionTooLow = "urn:oasis:names:tc:SAML:2.0:status:RequestVersionTooLow";

    /// <summary>The second-level resource-not-recognized status.</summary>
    public const string ResourceNotRecognized = "urn:oasis:names:tc:SAML:2.0:status:ResourceNotRecognized";

    /// <summary>The second-level too-many-responses status.</summary>
    public const string TooManyResponses = "urn:oasis:names:tc:SAML:2.0:status:TooManyResponses";

    /// <summary>The second-level unknown-attribute-profile status.</summary>
    public const string UnknownAttrProfile = "urn:oasis:names:tc:SAML:2.0:status:UnknownAttrProfile";

    /// <summary>The second-level unknown-principal status.</summary>
    public const string UnknownPrincipal = "urn:oasis:names:tc:SAML:2.0:status:UnknownPrincipal";

    /// <summary>The second-level unsupported-binding status.</summary>
    public const string UnsupportedBinding = "urn:oasis:names:tc:SAML:2.0:status:UnsupportedBinding";
}
