namespace Assimalign.Cohesion.Database.Transactions;

/// <summary>
/// Creates <see cref="ILockManager"/> instances.
/// </summary>
public static class LockManager
{
    /// <summary>
    /// Creates the default lock manager: a mode-compatibility lock table with FIFO
    /// waiter wake-up and wait-for-graph deadlock detection that aborts the
    /// requester whose wait would close a cycle.
    /// </summary>
    /// <returns>The lock manager.</returns>
    public static ILockManager Create() => new DefaultLockManager();
}
