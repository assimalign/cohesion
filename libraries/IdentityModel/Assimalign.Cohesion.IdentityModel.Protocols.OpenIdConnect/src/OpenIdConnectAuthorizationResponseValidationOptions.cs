namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Carries the requesting context an authorization response is validated against.
/// </summary>
public sealed class OpenIdConnectAuthorizationResponseValidationOptions
{
    /// <summary>
    /// Gets or sets the <c>state</c> value the request sent; a mismatching echo is an
    /// error. Null skips the check.
    /// </summary>
    public string? ExpectedCorrelationState { get; set; }

    /// <summary>
    /// Gets or sets the issuer the response is expected from (RFC 9207 mix-up defense).
    /// Null skips the check.
    /// </summary>
    public string? ExpectedIssuer { get; set; }

    /// <summary>
    /// Gets or sets whether the provider advertises the RFC 9207 <c>iss</c> response
    /// parameter (<c>authorization_response_iss_parameter_supported</c>); when true, an
    /// absent parameter is reported.
    /// </summary>
    public bool IssuerParameterAdvertised { get; set; }

    /// <summary>
    /// Gets or sets the <c>response_type</c> the request asked for; a success response
    /// missing a promised artifact is an error. Null skips the check.
    /// </summary>
    public string? ExpectedResponseType { get; set; }
}
