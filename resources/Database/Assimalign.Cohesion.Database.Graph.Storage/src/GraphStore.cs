using System;
using Assimalign.Cohesion.Database.Graph.Storage.Internal;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>Opens the graph record and index facade over a shared coordinator.</summary>
public static class GraphStore
{
    /// <summary>Loads graph directories and physical B+Tree registrations.</summary>
    /// <param name="storage">Graph file set.</param><param name="coordinator">Coordinator bound to the same file set.</param><returns>The graph store.</returns>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public static IGraphStore Open(GraphStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        return new DefaultGraphStore(storage, coordinator);
    }

    /// <summary>
    /// Checks that every index tree the storage registers is in the B-tree page format
    /// this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>), reading only
    /// the registrations and each tree's root page. An engine calls it before the
    /// coordinator's recovery scrub, so a database whose indexes it cannot read is
    /// refused before anything is written to it; <see cref="Open"/> checks again as it
    /// attaches the trees.
    /// </summary>
    /// <param name="storage">Graph file set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    /// <exception cref="StorageCorruptionException">A persisted index registration is malformed.</exception>
    public static void EnsureIndexFormat(GraphStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        DefaultGraphStore.EnsureIndexFormat(storage);
    }
}
