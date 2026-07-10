namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// The mode a lock is requested or held in.
/// </summary>
public enum LockMode : byte
{
    /// <summary>
    /// A shared read lock. Compatible with other shared and intent-shared locks.
    /// </summary>
    Shared = 0,

    /// <summary>
    /// A read lock taken with intent to write. Compatible with shared locks but
    /// not with other update or exclusive locks; prevents lock-upgrade deadlocks.
    /// </summary>
    Update,

    /// <summary>
    /// An exclusive write lock. Incompatible with every other mode.
    /// </summary>
    Exclusive,

    /// <summary>
    /// Declares intent to take shared locks on children of this resource.
    /// </summary>
    IntentShared,

    /// <summary>
    /// Declares intent to take exclusive locks on children of this resource.
    /// </summary>
    IntentExclusive,
}
