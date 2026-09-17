using System;

using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>Opens a document metadata catalog over the database's shared content storage.</summary>
public static class DocumentCatalog
{
    /// <summary>Loads the metadata directory after the coordinator's recovery scrub.</summary>
    /// <param name="storage">The same storage that holds chunk records.</param>
    /// <param name="coordinator">The coordinator bound to that storage and its record space.</param>
    /// <returns>The database's metadata catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="DocumentCatalogException">Persisted catalog metadata is malformed or unsupported.</exception>
    public static IDocumentCatalog Open(DocumentStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        return DefaultDocumentCatalog.Open(storage, coordinator);
    }
}

