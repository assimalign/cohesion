using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>
/// A named collection of versioned documents.
/// </summary>
/// <remarks>
/// Operations take the session they execute in so document reads and writes share
/// the session's transaction and MVCC snapshot. Optimistic concurrency uses
/// <see cref="DocumentVersion"/>: pass the expected version to make a write
/// conditional; a mismatch fails with a <see cref="DatabaseException"/>.
/// </remarks>
public interface IDocumentCollection
{
    /// <summary>
    /// Gets the name of the collection, unique within its database.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reads a document by identity.
    /// </summary>
    /// <param name="session">The session the read executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The document, or null when no visible document has the identity.</returns>
    ValueTask<Document?> GetAsync(IDatabaseSession session, DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a document, inserting or replacing by identity.
    /// </summary>
    /// <param name="session">The session the write executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="content">The document content as UTF-8 JSON.</param>
    /// <param name="expectedVersion">When set, the write succeeds only if the stored version matches.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The stored document with its new version.</returns>
    /// <exception cref="DatabaseException">Thrown when <paramref name="expectedVersion"/> is set and does not match.</exception>
    ValueTask<Document> PutAsync(IDatabaseSession session, DocumentId id, ReadOnlyMemory<byte> content, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document by identity.
    /// </summary>
    /// <param name="session">The session the delete executes in.</param>
    /// <param name="id">The document identity.</param>
    /// <param name="expectedVersion">When set, the delete succeeds only if the stored version matches.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when a document was deleted; false when none was visible.</returns>
    /// <exception cref="DatabaseException">Thrown when <paramref name="expectedVersion"/> is set and does not match.</exception>
    ValueTask<bool> DeleteAsync(IDatabaseSession session, DocumentId id, DocumentVersion? expectedVersion = null, CancellationToken cancellationToken = default);
}
