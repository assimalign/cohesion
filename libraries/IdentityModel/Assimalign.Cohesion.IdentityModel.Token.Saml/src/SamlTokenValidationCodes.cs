namespace Assimalign.Cohesion.IdentityModel.Token.Saml;

/// <summary>
/// Defines the SAML-token-minted validation diagnostic codes. Cross-cutting concepts (issuer,
/// audience, temporal, missing required member) use the shared <see cref="TokenValidationCodes" />;
/// this class carries only diagnostics the SAML assertion-token layer itself defines.
/// </summary>
public static class SamlTokenValidationCodes
{
    /// <summary>
    /// No bearer subject confirmation satisfies the confirmation-data window rules. Mirrors the
    /// protocol branch's <c>SamlValidationCodes.SubjectConfirmationInvalid</c> value so
    /// cross-branch tooling groups the finding.
    /// </summary>
    public const string SubjectConfirmationInvalid = "subject_confirmation_invalid";
}
