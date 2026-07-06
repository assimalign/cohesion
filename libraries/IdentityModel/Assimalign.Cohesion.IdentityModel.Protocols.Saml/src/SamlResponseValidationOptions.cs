namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Carries the receiving context a SAML response is validated against.
/// </summary>
public sealed class SamlResponseValidationOptions
{
    /// <summary>
    /// Gets or sets the identifier of the request this response answers. The response's
    /// <c>InResponseTo</c> must match it; leave null for identity-provider-initiated single
    /// sign-on. Null skips the check.
    /// </summary>
    public string? ExpectedInResponseTo { get; set; }

    /// <summary>
    /// Gets or sets the endpoint the response was received at. The response's
    /// <c>Destination</c> must match it. Null skips the check.
    /// </summary>
    public string? ExpectedDestination { get; set; }
}
