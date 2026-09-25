using System;

using Assimalign.Cohesion.Database.Graph.Catalog.Internal;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Catalog;

/// <summary>Opens the metadata catalog belonging to one logical graph database.</summary>
public static class GraphCatalog
{
    /// <summary>Loads the catalog after shared recovery has scrubbed abandoned writers.</summary>
    /// <param name="storage">The database's graph storage.</param>
    /// <param name="coordinator">The coordinator bound to that storage.</param>
    /// <returns>The snapshot-visible catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="GraphCatalogException">Persisted metadata is malformed.</exception>
    public static IGraphCatalog Open(GraphStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        return DefaultGraphCatalog.Open(storage, coordinator);
    }
}
