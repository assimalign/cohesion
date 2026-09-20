using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Catalog;

/// <summary>The snapshot-visible collection and document metadata of one logical database.</summary>
/// <remarks>
/// Mutations join the supplied logical transaction and do not commit it. The caller
/// acquires model locks and resolves write conflicts before invoking a mutation.
/// Listing uses the metadata directory and never traverses document content.
/// </remarks>
public interface IDocumentCatalog
{
    /// <summary>Lists visible index definitions in ordinal name order.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="snapshot">The visibility snapshot.</param>
    /// <returns>The visible definitions.</returns>
    IReadOnlyList<DocumentIndexMetadata> GetIndexes(Guid collectionId, TransactionSnapshot snapshot);

    /// <summary>Builds a nonunique B+Tree and publishes its definition in the supplied transaction.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The new index name.</param>
    /// <param name="path">The scalar field path.</param>
    /// <param name="context">The active transaction; the caller holds the collection's exclusive lock.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <returns>The new index definition.</returns>
    /// <exception cref="DocumentCatalogException">The collection is absent or index already exists.</exception>
    ValueTask<DocumentIndexMetadata> CreateIndexAsync(Guid collectionId, string name, string path, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones an index definition, retaining its tree for older snapshots.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="name">The index name.</param>
    /// <param name="context">The active transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing the operation.</returns>
    ValueTask DeleteIndexAsync(Guid collectionId, string name, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Seeks a scalar key range and returns visible documents in ordinal identity order.</summary>
    /// <param name="collectionId">The collection identity.</param>
    /// <param name="indexName">The visible index name.</param>
    /// <param name="lower">The inclusive or exclusive scalar lower bound, or null for unbounded.</param>
    /// <param name="includeLower">Whether the lower bound is included.</param>
    /// <param name="upper">The scalar upper bound, or null for unbounded.</param>
    /// <param name="includeUpper">Whether the upper bound is included.</param>
    /// <param name="snapshot">The visibility snapshot shared with the residual predicate.</param>
    /// <param name="cancellationToken">Cancels iteration.</param>
    /// <returns>The matching visible metadata.</returns>
    ValueTask<IReadOnlyList<DocumentCatalogEntry>> SearchIndexAsync(Guid collectionId, string indexName, object? lower, bool includeLower, object? upper, bool includeUpper, TransactionSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Scrubs abandoned writers from every persisted B+Tree before recovery checkpointing.</summary>
    /// <param name="writers">The uncommitted writers identified by shared recovery.</param>
    /// <param name="cancellationToken">Cancels the scrub.</param>
    /// <returns>A task representing the scrub.</returns>
    ValueTask RecoverIndexesAsync(IReadOnlySet<TransactionSequence> writers, CancellationToken cancellationToken = default);

    /// <summary>Finds a collection visible through a snapshot.</summary>
    /// <param name="name">The case-sensitive collection name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible collection, or null.</returns>
    DocumentCollectionMetadata? FindCollection(string name, TransactionSnapshot snapshot);

    /// <summary>Lists collections visible through a snapshot, ordered by ordinal name.</summary>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible collection metadata.</returns>
    IReadOnlyList<DocumentCollectionMetadata> GetCollections(TransactionSnapshot snapshot);

    /// <summary>Creates or replaces a collection metadata version in the supplied transaction.</summary>
    /// <param name="collection">The complete collection metadata.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentException">The collection identity or name is invalid.</exception>
    ValueTask SaveCollectionAsync(DocumentCollectionMetadata collection, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones a collection metadata version in the supplied transaction.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller handles ownership enforcement and deletion of contained documents.</remarks>
    ValueTask DeleteCollectionAsync(Guid collectionId, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Finds a document metadata version visible through a snapshot.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="name">The case-sensitive document name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible document metadata, or null.</returns>
    DocumentCatalogEntry? FindDocument(Guid collectionId, string name, TransactionSnapshot snapshot);

    /// <summary>Lists visible document metadata, optionally filtered by an ordinal name prefix.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="prefix">The case-sensitive name prefix, or null for all names.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible document metadata ordered by ordinal name.</returns>
    IReadOnlyList<DocumentCatalogEntry> GetDocuments(Guid collectionId, string? prefix, TransactionSnapshot snapshot);

    /// <summary>Creates or replaces document metadata in the supplied transaction.</summary>
    /// <param name="document">The complete document metadata and chunk head reference.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentException">The document identity, name, or length is invalid.</exception>
    ValueTask SaveDocumentAsync(DocumentCatalogEntry document, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones document metadata in the supplied transaction.</summary>
    /// <param name="collectionId">The stable collection identity.</param>
    /// <param name="name">The case-sensitive document name.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller tombstones the content chain in the same logical transaction.</remarks>
    ValueTask DeleteDocumentAsync(Guid collectionId, string name, ITransactionContext context, CancellationToken cancellationToken = default);
}

