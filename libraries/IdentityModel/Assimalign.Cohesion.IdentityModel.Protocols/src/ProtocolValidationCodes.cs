namespace Assimalign.Cohesion.IdentityModel.Protocols;

/// <summary>
/// Defines the cross-protocol validation diagnostic codes. Diagnostics are the family's
/// shared currency: findings that mean the same thing in OpenID Connect and SAML use one
/// code from this class, so cross-protocol tooling can group them; concepts minted by a
/// single protocol get codes in that protocol branch's own constants class.
/// </summary>
public static class ProtocolValidationCodes
{
    /// <summary>
    /// A member the governing specification requires is absent.
    /// </summary>
    public const string MissingRequiredMember = "missing_required_member";

    /// <summary>
    /// A member the governing specification recommends is absent.
    /// </summary>
    public const string MissingRecommendedMember = "missing_recommended_member";

    /// <summary>
    /// A member that must be an absolute URI or endpoint location is malformed.
    /// </summary>
    public const string InvalidEndpoint = "invalid_endpoint";

    /// <summary>
    /// The asserted issuer does not match the expected issuer.
    /// </summary>
    public const string IssuerMismatch = "issuer_mismatch";

    /// <summary>
    /// The audience does not include the expected party, or includes parties that are not
    /// trusted.
    /// </summary>
    public const string AudienceMismatch = "audience_mismatch";

    /// <summary>
    /// A correlation value (state, relay state, or message-identifier echo) does not match
    /// the expected value.
    /// </summary>
    public const string CorrelationMismatch = "correlation_mismatch";

    /// <summary>
    /// The artifact is expired at the validation instant.
    /// </summary>
    public const string Expired = "expired";

    /// <summary>
    /// The artifact is not yet valid at the validation instant.
    /// </summary>
    public const string NotYetValid = "not_yet_valid";

    /// <summary>
    /// The asserted subject does not match the expected subject.
    /// </summary>
    public const string SubjectMismatch = "subject_mismatch";

    /// <summary>
    /// A member carries a value the governing specification does not allow.
    /// </summary>
    public const string ValueNotAllowed = "value_not_allowed";
}
