using System;

using Assimalign.Cohesion.Database.Blob.Catalog.Internal;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>Opens a blob metadata catalog over the database's shared content storage.</summary>
public static class BlobCatalog
{
    /// <summary>Loads the metadata directory after the coordinator's recovery scrub.</summary>
    /// <param name="storage">The same storage that holds chunk records.</param>
    /// <param name="coordinator">The coordinator bound to that storage and its record space.</param>
    /// <returns>The database's metadata catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="BlobCatalogException">Persisted catalog metadata is malformed or unsupported.</exception>
    public static IBlobCatalog Open(BlobStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        return DefaultBlobCatalog.Open(storage, coordinator);
    }
}
