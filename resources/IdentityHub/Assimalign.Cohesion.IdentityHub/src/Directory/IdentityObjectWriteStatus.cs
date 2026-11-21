namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The outcome of a write to an <see cref="IIdentityObjectRegistry{TObject, TId}"/>. The member
/// values are stable and additive-only.
/// </summary>
/// <remarks>
/// A refused write is an ordinary outcome, not a failure, and never throws: it is how a caller
/// learns that an object is locked, that another caller changed it first, or that a name it
/// wanted is taken.
/// </remarks>
public enum IdentityObjectWriteStatus
{
    /// <summary>
    /// No write was attempted. This is the status of a <see langword="default"/> result.
    /// </summary>
    None = 0,

    /// <summary>
    /// The object was stored and the result carries its new version.
    /// </summary>
    Written = 1,

    /// <summary>
    /// The object was removed.
    /// </summary>
    Removed = 2,

    /// <summary>
    /// The write was refused because the identifier belongs to an object defined in code. Such
    /// an object changes only where it is defined.
    /// </summary>
    Locked = 3,

    /// <summary>
    /// An add was refused because a stored object already holds the identifier.
    /// </summary>
    IdConflict = 4,

    /// <summary>
    /// A replace or a remove was refused because no stored object holds the identifier, or its
    /// version is not the expected one.
    /// </summary>
    VersionConflict = 5,

    /// <summary>
    /// An add or a replace was refused because another object of the same kind already holds
    /// the name.
    /// </summary>
    NameConflict = 6,
}
