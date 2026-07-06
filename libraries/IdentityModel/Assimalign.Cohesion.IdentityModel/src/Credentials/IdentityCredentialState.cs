namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Represents the administrative state of an <see cref="IdentityCredential" />.
/// </summary>
/// <remarks>
/// Temporal expiry is deliberately not a state: it is computed from
/// <see cref="IdentityCredential.NotBefore" /> and <see cref="IdentityCredential.ExpiresAt" />
/// by <see cref="IdentityCredential.IsUsable" />, so the model has a single source of truth
/// for time-based validity. <see cref="Unknown" /> is the default so that a forgotten state
/// assignment can never silently produce a usable credential.
/// </remarks>
public enum IdentityCredentialState
{
    /// <summary>
    /// The credential state is not known. Credentials in this state are never usable.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The credential is active.
    /// </summary>
    Active = 1,

    /// <summary>
    /// The credential is administratively suspended and may later return to
    /// <see cref="Active" />.
    /// </summary>
    Suspended = 2,

    /// <summary>
    /// The credential is permanently revoked.
    /// </summary>
    Revoked = 3
}
