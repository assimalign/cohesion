using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>The snapshot-visible container and blob metadata of one logical database.</summary>
/// <remarks>
/// Mutations join the supplied logical transaction and do not commit it. The caller
/// acquires model locks and resolves write conflicts before invoking a mutation.
/// Listing uses the metadata directory and never traverses blob content.
/// </remarks>
public interface IBlobCatalog
{
    /// <summary>Finds a container visible through a snapshot.</summary>
    /// <param name="name">The case-sensitive container name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible container, or null.</returns>
    BlobContainerMetadata? FindContainer(string name, TransactionSnapshot snapshot);

    /// <summary>Lists containers visible through a snapshot, ordered by ordinal name.</summary>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible container metadata.</returns>
    IReadOnlyList<BlobContainerMetadata> GetContainers(TransactionSnapshot snapshot);

    /// <summary>Creates or replaces a container metadata version in the supplied transaction.</summary>
    /// <param name="container">The complete container metadata.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentException">The container identity or name is invalid.</exception>
    ValueTask SaveContainerAsync(BlobContainerMetadata container, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones a container metadata version in the supplied transaction.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller handles ownership enforcement and deletion of contained blobs.</remarks>
    ValueTask DeleteContainerAsync(Guid containerId, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Finds a blob metadata version visible through a snapshot.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="name">The case-sensitive blob name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible blob metadata, or null.</returns>
    BlobCatalogEntry? FindBlob(Guid containerId, string name, TransactionSnapshot snapshot);

    /// <summary>Lists visible blob metadata, optionally filtered by an ordinal name prefix.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="prefix">The case-sensitive name prefix, or null for all names.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible blob metadata ordered by ordinal name.</returns>
    IReadOnlyList<BlobCatalogEntry> GetBlobs(Guid containerId, string? prefix, TransactionSnapshot snapshot);

    /// <summary>Creates or replaces blob metadata in the supplied transaction.</summary>
    /// <param name="blob">The complete blob metadata and chunk head reference.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentException">The blob identity, name, or length is invalid.</exception>
    ValueTask SaveBlobAsync(BlobCatalogEntry blob, ITransactionContext context, CancellationToken cancellationToken = default);

    /// <summary>Tombstones blob metadata in the supplied transaction.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="name">The case-sensitive blob name.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller tombstones the content chain in the same logical transaction.</remarks>
    ValueTask DeleteBlobAsync(Guid containerId, string name, ITransactionContext context, CancellationToken cancellationToken = default);
}
