using System;
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
}
