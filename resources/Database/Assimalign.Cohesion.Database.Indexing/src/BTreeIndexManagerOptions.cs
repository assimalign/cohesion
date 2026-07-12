using System.Collections.Generic;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Composition options for a B+Tree index manager.
/// </summary>
public sealed class BTreeIndexManagerOptions
{
    /// <summary>
    /// Gets or sets the storage instance whose pages back the indexes.
    /// </summary>
    public required IStorage Storage { get; init; }

    /// <summary>
    /// Gets or sets the resolver pairing logical transactions with their storage
    /// transactions, so index mutations ride the owning write-ahead scope.
    /// </summary>
    public required IStorageTransactionSource TransactionSource { get; init; }

    /// <summary>
    /// Gets or sets the lock manager unique indexes arbitrate concurrent key
    /// writers through. Optional: without it, unique enforcement still checks
    /// visible state, but concurrent uncommitted writers of the same key are only
    /// serialized by the page write locks.
    /// </summary>
    public ILockManager? LockManager { get; init; }

    /// <summary>
    /// Gets or sets the registrations of indexes that already exist in storage
    /// (exported by the catalog at its last persistence point).
    /// </summary>
    public IReadOnlyList<BTreeIndexRegistration>? ExistingIndexes { get; init; }
}
