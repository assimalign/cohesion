namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the administrative state of an <see cref="AuthenticationSession" />.
/// </summary>
/// <remarks>
/// Temporal expiry is deliberately not a state: it is computed from
/// <see cref="AuthenticationSession.ExpiresAt" /> by
/// <see cref="AuthenticationSession.IsActive" />, so the model has a single source of truth
/// for time-based validity. <see cref="Unknown" /> is the default so that a forgotten state
/// assignment (for example a session-store rehydration that fails to map the persisted
/// state) can never silently produce a live session — the same fail-closed rule
/// <see cref="IdentityCredentialState" /> applies to credentials.
/// </remarks>
public enum AuthenticationSessionState
{
    /// <summary>
    /// The session state is not known. Sessions in this state are never active.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The session is active.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The session was terminated (logout, administrative revocation, or provider-initiated
    /// single logout).
    /// </summary>
    Terminated = 2
}
