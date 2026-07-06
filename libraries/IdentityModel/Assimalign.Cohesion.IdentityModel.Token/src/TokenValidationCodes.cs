namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Defines the token validation diagnostic codes for the protocol-neutral data rules. These
/// mirror the corresponding cross-protocol codes by string value so cross-branch tooling can
/// group findings, but are declared here because the token branch must not reference the
/// protocol branch.
/// </summary>
public static class TokenValidationCodes
{
    /// <summary>The token issuer does not match the expected issuer.</summary>
    public const string IssuerMismatch = "issuer_mismatch";

    /// <summary>The token is not intended for the expected audience.</summary>
    public const string AudienceMismatch = "audience_mismatch";

    /// <summary>The token is expired at the validation instant.</summary>
    public const string Expired = "expired";

    /// <summary>The token is not yet valid at the validation instant.</summary>
    public const string NotYetValid = "not_yet_valid";

    /// <summary>
    /// A member the governing specification requires is absent. Mirrors the protocol branch's
    /// <c>ProtocolValidationCodes.MissingRequiredMember</c> value so cross-branch tooling can
    /// group the finding.
    /// </summary>
    public const string MissingRequiredMember = "missing_required_member";
}
