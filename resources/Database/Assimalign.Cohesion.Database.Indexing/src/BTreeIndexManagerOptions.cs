using System;
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
    public required Storage.Storage Storage { get; init; }

    /// <summary>
    /// Gets or sets the resolver pairing logical transactions with their storage
    /// transactions, so index mutations ride the owning write-ahead scope: given the
    /// transaction an index mutation belongs to, it returns the storage transaction (the
    /// statement bracket) the mutation's page changes join.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The engine owns the pairing: it begins a storage transaction for each statement of a
    /// logical transaction and keeps the mapping while the statement applies (in practice
    /// <c>TransactionCoordinator.TryGetStorageTransaction</c>). A crash mid-way therefore rolls
    /// index and data changes back as one unit.
    /// </para>
    /// <para>
    /// When no bracket is paired, the resolver throws, in the engine's own exception
    /// vocabulary: the Sql and KeyValuePair engines raise their area-level database exception,
    /// and the Documents catalog and the Graph store an <see cref="InvalidOperationException"/>.
    /// A delegate, not a type, so that this child root never references the area root's
    /// exceptions (#1258; it replaced the <c>IStorageTransactionSource</c> interface and the four
    /// engine wrappers that implemented it).
    /// </para>
    /// </remarks>
    public required Func<TransactionContext, StorageTransaction> TransactionSource { get; init; }

    /// <summary>
    /// Gets or sets the lock manager unique indexes arbitrate concurrent key
    /// writers through. Optional: without it, unique enforcement still checks
    /// visible state, but concurrent uncommitted writers of the same key are only
    /// serialized by the page write locks.
    /// </summary>
    public LockManager? LockManager { get; init; }

    /// <summary>
    /// Gets or sets the registrations of indexes that already exist in storage
    /// (exported by the catalog at its last persistence point).
    /// </summary>
    public IReadOnlyList<BTreeIndexRegistration>? ExistingIndexes { get; init; }
}
