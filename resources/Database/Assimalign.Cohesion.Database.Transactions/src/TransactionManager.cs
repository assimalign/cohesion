using System;

namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Creates <see cref="ITransactionManager"/> instances. One manager serves one
/// logical database; engines compose it from a transaction log (the write-ahead
/// binding), a lock manager, and a version store.
/// </summary>
public static class TransactionManager
{
    /// <summary>
    /// Creates a transaction manager over the specified collaborators.
    /// </summary>
    /// <param name="log">The transaction log enforcing the write-ahead rule.</param>
    /// <param name="lockManager">The lock manager arbitrating write-write conflicts.</param>
    /// <param name="versionStore">The version store rows/documents resolve through.</param>
    /// <returns>The transaction manager.</returns>
    public static ITransactionManager Create(ITransactionLog log, ILockManager lockManager, IVersionStore versionStore)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(lockManager);
        ArgumentNullException.ThrowIfNull(versionStore);

        return new DefaultTransactionManager(log, lockManager, versionStore);
    }
}
