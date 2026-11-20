namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The outcome of a conditional write to an <see cref="IIdentityHubStore"/>. The member values
/// are stable and additive-only.
/// </summary>
/// <remarks>
/// A refused write is an ordinary outcome, not a failure: it is how a caller learns that it
/// lost a race, and it never throws.
/// </remarks>
public enum IdentityHubWriteStatus
{
    /// <summary>
    /// No write was attempted. This is the status of a <see langword="default"/> result.
    /// </summary>
    None = 0,

    /// <summary>
    /// The record was stored and the result carries its new version.
    /// </summary>
    Written = 1,

    /// <summary>
    /// An add was refused because a live record already holds the key.
    /// </summary>
    KeyConflict = 2,

    /// <summary>
    /// A replace was refused because no live record holds the key, or the record's version is
    /// not the expected one.
    /// </summary>
    VersionConflict = 3,

    /// <summary>
    /// The write was refused because another live record lists one of the record's unique
    /// index entries. The result names the index.
    /// </summary>
    IndexConflict = 4,
}
