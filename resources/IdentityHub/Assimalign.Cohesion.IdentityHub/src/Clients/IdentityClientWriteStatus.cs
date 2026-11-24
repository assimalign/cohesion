namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The outcome of a write to an <see cref="IIdentityClientRegistry"/>. The member values are
/// stable and additive-only.
/// </summary>
/// <remarks>
/// A refused write is an ordinary outcome, not a failure, and never throws: it is how a caller
/// learns that a registration is locked, that another caller changed it first, or that a name
/// it wanted is taken.
/// </remarks>
public enum IdentityClientWriteStatus
{
    /// <summary>
    /// No write was attempted. This is the status of a <see langword="default"/> result.
    /// </summary>
    None = 0,

    /// <summary>
    /// The registration was stored and the result carries its new version.
    /// </summary>
    Written = 1,

    /// <summary>
    /// The registration was removed.
    /// </summary>
    Removed = 2,

    /// <summary>
    /// The write was refused because the identifier belongs to a registration defined in code.
    /// Such a registration changes only where it is defined.
    /// </summary>
    Locked = 3,

    /// <summary>
    /// An add was refused because a stored registration already holds the identifier.
    /// </summary>
    IdConflict = 4,

    /// <summary>
    /// A replace or a remove was refused because no stored registration holds the identifier,
    /// or its version is not the expected one.
    /// </summary>
    VersionConflict = 5,

    /// <summary>
    /// An add or a replace was refused because another registration already exposes one of the
    /// registration's audiences. The result names the audience when the registry can.
    /// </summary>
    AudienceConflict = 6,
}
