using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Catalog;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>
/// A named container of streamed large objects.
/// </summary>
/// <remarks>
/// <para>
/// Blob content is streamed and never buffered whole; the metadata catalog is transactional. A
/// blob written through <see cref="OpenWriteAsync"/> becomes visible atomically when the returned
/// stream is disposed after a successful write — readers never observe partial content.
/// </para>
/// <para>
/// <b>Binding.</b> A container is bound to the session that returned it
/// (<see cref="BlobDatabaseSession.GetContainerAsync"/> and its siblings): each of its operations
/// is an operation of the session, in its explicit transaction when one is open, and holds the
/// session from its start to its end (a stream's end is its disposal). Every container comes from
/// a session: the database has no container operations of its own (owner decision 32 of
/// 2026-10-06), so no container operation runs outside a session.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> A public sealed type with an internal
/// constructor, replacing the former <c>IBlobContainer</c> interface and its internal
/// implementation; <see cref="GetOwnershipAsync"/>, which an extension container reached through
/// a cast to the implementation, is a member of the type.
/// </para>
/// </remarks>
public sealed class BlobContainer
{
    private readonly BlobDatabase _database;
    private readonly BlobContainerMetadata _container;
    private readonly BlobDatabaseSession _session;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlobContainer"/> class.
    /// </summary>
    /// <param name="database">The blob database that owns the container.</param>
    /// <param name="container">The catalog metadata of the container.</param>
    /// <param name="session">The session the container is bound to, whose operations its operations are.</param>
    internal BlobContainer(BlobDatabase database, BlobContainerMetadata container, BlobDatabaseSession session)
    {
        _database = database;
        _container = container;
        _session = session;
    }

    /// <summary>
    /// Gets the name of the container, unique within its database.
    /// </summary>
    public string Name => _container.Name;

    /// <summary>
    /// Gets the catalog record this handle addresses: the container's identity, which a container
    /// created later under the same name does not share. For this assembly's tests, which bind the
    /// identity to sessions of their own.
    /// </summary>
    internal BlobContainerMetadata Metadata => _container;

    /// <summary>
    /// Opens a stream that writes a blob's content. The blob becomes visible atomically when the
    /// stream is disposed after a complete write.
    /// </summary>
    /// <param name="name">The blob name, unique within the container.</param>
    /// <param name="options">Write options (content type, overwrite behavior), or null for defaults.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A writable stream for the blob content.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The blob exists and overwrite is not permitted, or the container is no longer available; for
    /// a container bound to a session, the session is closed, another operation holds it, or its
    /// transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The blob catalog changed since the transaction's snapshot (retryable).</exception>
    public async ValueTask<Stream> OpenWriteAsync(string name, BlobWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        bool overwrite = options?.Overwrite ?? true;
        string? contentType = options?.ContentType;
        var operation = await _database.BeginOperationAsync(_session, cancellationToken).ConfigureAwait(false);
        try
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var previous = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            if (previous != _database.Catalog.FindBlob(_container.Id, name, _database.LatestSnapshot(operation.Context)))
            {
                BlobDatabase.ThrowConflict();
            }

            if (previous is not null && !overwrite)
            {
                throw new DatabaseException($"Blob '{name}' already exists.");
            }

            var stream = _database.DataStorage.OpenWrite(_database.Coordinator, operation.Context, async content =>
            {
                operation.EnsureActive();
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null)
                {
                    await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context,
                        BlobDatabase.Content(previous.Value), cancellationToken).ConfigureAwait(false);
                }

                var now = DateTimeOffset.UtcNow;
                // Physical sequence allocation shares the durable allocator with
                // the coordinator, so even repeated writes in one transaction
                // receive distinct tags and no tag repeats after a restart.
                ulong etag = (ulong)_database.DataStorage.ReserveTransactionSequence();
                await _database.Catalog.SaveBlobAsync(new BlobCatalogEntry(_container.Id, name, content.Length, contentType,
                    etag, previous?.CreatedAt ?? now, now, content.Checksum, content.Head), operation.Context, cancellationToken).ConfigureAwait(false);
                await operation.CompleteAsync().ConfigureAwait(false);
            }, error => operation.AbortAsync(operation.TranslateFailure(error)), cancellationToken);
            return new BlobGuardedStream(stream, operation);
        }
        catch (Exception error)
        {
            // An explicit transaction records the error its caller sees as the cause of its abort.
            var reported = _database.TranslateFailure(error);
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    /// <summary>
    /// Opens a stream that reads a blob's content.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A readable stream over the blob content, which keeps its snapshot until disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The blob does not exist, or the container is no longer available; for a container bound to a
    /// session, the session is closed, another operation holds it, or its transaction refuses
    /// operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    public async ValueTask<Stream> OpenReadAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var operation = await _database.BeginOperationAsync(_session, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureContainer(operation.Context);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot)
                ?? throw new DatabaseException($"Blob '{name}' does not exist.");
            return new BlobGuardedStream(_database.DataStorage.OpenRead(BlobDatabase.Content(entry), operation.CompleteAsync), operation);
        }
        catch (Exception error)
        {
            var reported = _database.TranslateFailure(error);
            await operation.AbortAsync(reported).ConfigureAwait(false);
            if (ReferenceEquals(reported, error)) { throw; }
            throw reported;
        }
    }

    /// <summary>
    /// Reads a blob's metadata.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The blob's properties, or null when the blob does not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The container is no longer available; for a container bound to a session, the session is
    /// closed, another operation holds it, or its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    public ValueTask<BlobProperties?> GetPropertiesAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            return new ValueTask<BlobProperties?>(entry is null ? null : BlobDatabase.Properties(entry.Value));
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="name">The blob name.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>True when the blob was deleted; false when it did not exist.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null, empty or white space.</exception>
    /// <exception cref="ObjectDisposedException">The database has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The container is no longer available; for a container bound to a session, the session is
    /// closed, another operation holds it, or its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <exception cref="DatabaseTransactionAbortedException">The blob catalog changed since the transaction's snapshot (retryable).</exception>
    /// <exception cref="DatabaseTransactionCommitUnconfirmedException">An autocommit operation's commit record could not be confirmed durable.</exception>
    public ValueTask<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _database.RunAsync(_session, async operation =>
        {
            await _database.LockWriterAsync(operation.Context, cancellationToken).ConfigureAwait(false);
            EnsureContainer(operation.Context, writing: true);
            var entry = _database.Catalog.FindBlob(_container.Id, name, operation.Context.Snapshot);
            if (entry != _database.Catalog.FindBlob(_container.Id, name, _database.LatestSnapshot(operation.Context)))
            {
                BlobDatabase.ThrowConflict();
            }

            if (entry is null)
            {
                return false;
            }

            await _database.DataStorage.TombstoneContentAsync(_database.Coordinator, operation.Context,
                BlobDatabase.Content(entry.Value), cancellationToken).ConfigureAwait(false);
            await _database.Catalog.DeleteBlobAsync(_container.Id, name, operation.Context, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    /// <summary>
    /// Streams the metadata of blobs in the container, in name order, read in one operation when the
    /// enumeration starts.
    /// </summary>
    /// <param name="prefix">When set, only blobs whose names start with the prefix are returned.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>An async sequence of blob properties.</returns>
    /// <exception cref="ObjectDisposedException">The database has been disposed (also while the properties are yielded).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The container is no longer available; for a container bound to a session, the session is
    /// closed (also while the properties are yielded), another operation holds it, or its
    /// transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    public async IAsyncEnumerable<BlobProperties> GetBlobsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            return new ValueTask<IReadOnlyList<BlobCatalogEntry>>(_database.Catalog.GetBlobs(_container.Id, prefix, operation.Context.Snapshot));
        }, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _database.EnsureNotDisposed();
            _session.ThrowIfNotOpen();
            yield return BlobDatabase.Properties(entry);
        }
    }

    /// <summary>Reads the container's ownership from the current operation's catalog snapshot.</summary>
    /// <param name="cancellationToken">Cancels the metadata read.</param>
    /// <returns>
    /// A read-only property bag containing <c>OWNER</c> as <see cref="DatabaseObjectOwner"/>
    /// and <c>OWNING_SCHEMA</c> as a schema name, or null for an ad-hoc container.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The database is disposed.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <exception cref="DatabaseException">
    /// The container is no longer available; for a container bound to a session, the session is
    /// closed, another operation holds it, or its transaction refuses operations (<c>COHDBB001</c>).
    /// </exception>
    /// <exception cref="DatabaseOfflineException">The database is offline (<c>COHDBB002</c>, #1243).</exception>
    /// <remarks>
    /// The read uses the bound session's isolation level. The returned property bag is
    /// detached from the catalog; all dictionary mutation operations throw <see cref="NotSupportedException"/>.
    /// Container discovery remains available through <see cref="BlobDatabaseSession.GetContainersAsync"/>.
    /// Before phase 4 of the
    /// concrete-types plan this was an extension over the container interface that cast to the
    /// implementation.
    /// </remarks>
    public ValueTask<IReadOnlyDictionary<string, object?>> GetOwnershipAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _database.RunAsync(_session, operation =>
        {
            EnsureContainer(operation.Context);
            var current = _database.Catalog.FindContainer(_container.Name, operation.Context.Snapshot)!.Value;
            return new ValueTask<IReadOnlyDictionary<string, object?>>(
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["OWNER"] = current.Owner,
                    ["OWNING_SCHEMA"] = current.OwningSchema,
                }));
        }, cancellationToken);
    }

    private void EnsureContainer(TransactionContext context, bool writing = false)
    {
        var current = _database.Catalog.FindContainer(Name, context.Snapshot);
        if (current?.Id != _container.Id)
        {
            throw new DatabaseException($"Container '{Name}' is no longer available in this transaction.");
        }

        if (writing && current != _database.Catalog.FindContainer(Name, _database.LatestSnapshot(context)))
        {
            BlobDatabase.ThrowConflict();
        }
    }
}
