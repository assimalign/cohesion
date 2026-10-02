using System;

using Assimalign.Cohesion.Database.Documents.Catalog.Internal;
using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Indexing;
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

    /// <summary>
    /// Checks that every index tree the storage registers is in the B-tree page format
    /// this engine reads (<see cref="BTreeIndexManager.FormatVersion"/>), reading only
    /// the registrations and each tree's root page. An engine calls it before the
    /// coordinator's recovery scrub, so a database whose indexes it cannot read is
    /// refused before anything is written to it; <see cref="Open"/> checks again as it
    /// attaches the trees.
    /// </summary>
    /// <param name="storage">The storage that holds the index registrations and trees.</param>
    /// <exception cref="ArgumentNullException"><paramref name="storage"/> is null.</exception>
    /// <exception cref="IndexFormatException">An index tree is in another B-tree page format.</exception>
    /// <exception cref="DocumentCatalogException">A persisted index registration is malformed.</exception>
    public static void EnsureIndexFormat(DocumentStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        DefaultDocumentCatalog.EnsureIndexFormat(storage);
    }
}

