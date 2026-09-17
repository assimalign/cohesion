using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Documents.Internal;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Manages collection indexes through the frozen document database seam.</summary>
public static class DocumentIndexExtensions
{
    extension(IDocumentDatabase database)
    {
        /// <summary>Creates a secondary B+Tree index on a scalar document path.</summary>
        /// <param name="collectionName">The collection within this database.</param>
        /// <param name="indexName">The index name, unique within the collection.</param>
        /// <param name="path">The case-sensitive field path, including optional array subscripts.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task that completes after index creation joins the session transaction or commits automatically.</returns>
        /// <exception cref="ArgumentException">A name or path is empty.</exception>
        /// <exception cref="DatabaseException">The collection or database implementation is unavailable.</exception>
        /// <exception cref="DatabaseObjectLockedException">The collection belongs to a schema.</exception>
        public ValueTask CreateIndexAsync(string collectionName, string indexName, string path, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            return Change(database, collectionName, indexName, path, cancellationToken);
        }

        /// <summary>Drops a collection's secondary index in the current session transaction or an automatic transaction.</summary>
        /// <param name="collectionName">The collection within this database.</param>
        /// <param name="indexName">The index to drop.</param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        /// <returns>A task representing index removal.</returns>
        /// <exception cref="ArgumentException">A name is empty.</exception>
        /// <exception cref="DatabaseException">The collection, index, or database implementation is unavailable.</exception>
        /// <exception cref="DatabaseObjectLockedException">The collection belongs to a schema.</exception>
        public ValueTask DropIndexAsync(string collectionName, string indexName, CancellationToken cancellationToken = default)
            => Change(database, collectionName, indexName, null, cancellationToken);
    }

    private static ValueTask Change(IDocumentDatabase database, string collectionName, string indexName, string? path, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database switch
        {
            DocumentDatabaseInstance instance => instance.ChangeIndexAsync(collectionName, indexName, path, null, token),
            DocumentSessionDatabase bound => bound.Instance.ChangeIndexAsync(collectionName, indexName, path, bound.Session, token),
            _ => throw new DatabaseException("This document database implementation does not expose collection index management.")
        };
    }
}
