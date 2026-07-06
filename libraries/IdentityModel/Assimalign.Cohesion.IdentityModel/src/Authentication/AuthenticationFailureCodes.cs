namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Defines the canonical authentication failure codes. Protocol-specific wire error codes
/// map onto these; the original wire code survives on
/// <see cref="AuthenticationFailure.OriginalCode" />.
/// </summary>
public static class AuthenticationFailureCodes
{
    /// <summary>
    /// The presented credentials are invalid.
    /// </summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>
    /// The presented credentials are expired or not yet valid.
    /// </summary>
    public const string ExpiredCredentials = "expired_credentials";

    /// <summary>
    /// The claimed subject does not exist.
    /// </summary>
    public const string SubjectNotFound = "subject_not_found";

    /// <summary>
    /// The claimed subject exists but is disabled or suspended.
    /// </summary>
    public const string SubjectDisabled = "subject_disabled";

    /// <summary>
    /// A required interaction (login, consent, step-up) is needed before authentication can
    /// complete.
    /// </summary>
    public const string InteractionRequired = "interaction_required";

    /// <summary>
    /// The protocol exchange itself failed (malformed message, unexpected response, or a
    /// wire-level error from the provider).
    /// </summary>
    public const string ProtocolError = "protocol_error";

    /// <summary>
    /// The protocol exchange completed but validation of the produced identity data failed.
    /// </summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>
    /// The failure does not map onto a more specific canonical code.
    /// </summary>
    public const string Unknown = "unknown";
}
