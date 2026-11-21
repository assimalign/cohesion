namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The category of an <see cref="IdentityHubException"/>, so a caller branches on the failure
/// without matching message text. The member values are stable and additive-only.
/// </summary>
public enum IdentityHubErrorCode
{
    /// <summary>
    /// The failure has no more specific category.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// The store could not complete an operation: it could not be reached, timed out, or failed
    /// on its medium. The operation's effect is unknown, and it may be tried again.
    /// </summary>
    StoreUnavailable = 1,

    /// <summary>
    /// A stored record could not be read back: its payload does not decode, or the stored
    /// state breaks a record limit. Trying again does not help.
    /// </summary>
    StoreRecordCorrupt = 2,

    /// <summary>
    /// A continuation token was not issued by the store for the query it was passed with.
    /// </summary>
    StoreContinuationInvalid = 3,
}
