using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Catalog.Internal;
using Assimalign.Cohesion.Database.Blob.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Catalog;

/// <summary>The snapshot-visible container and blob metadata of one logical database.</summary>
/// <remarks>
/// <para>
/// Mutations join the supplied logical transaction and do not commit it. The caller
/// acquires model locks and resolves write conflicts before invoking a mutation.
/// Listing uses the metadata directory and never traverses blob content.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, #1260).</b> One sealed type with a private
/// constructor behind <see cref="Open"/>: it replaces the former <c>IBlobCatalog</c>
/// interface, the <c>BlobCatalog</c> static factory and their internal implementation (the
/// triplet of <c>database-area.md</c> rule 1).
/// </para>
/// </remarks>
public sealed class BlobCatalog
{
    private readonly BlobStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private readonly Dictionary<string, List<Reference>> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid ContainerId, string Name), List<Reference>> _blobs = new();

    private BlobCatalog(BlobStorage storage, TransactionCoordinator coordinator)
    {
        _storage = storage;
        _coordinator = coordinator;
    }

    /// <summary>Loads the metadata directory after the coordinator's recovery scrub.</summary>
    /// <param name="storage">The same storage that holds chunk records.</param>
    /// <param name="coordinator">The coordinator bound to that storage and its record space.</param>
    /// <returns>The database's metadata catalog.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="BlobCatalogException">Persisted catalog metadata is malformed or unsupported.</exception>
    /// <remarks>
    /// The catalog keeps references to the storage and the coordinator but does not own them: the
    /// engine that opens it disposes them.
    /// </remarks>
    public static BlobCatalog Open(BlobStorage storage, TransactionCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(coordinator);
        var catalog = new BlobCatalog(storage, coordinator);
        // Owner zero is exclusively metadata; opening never materializes chunks.
        using var iterator = storage.GetUnitIterator(0);
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var record = BlobCatalogCodec.Decode(unit.Data.Span);
            var (writer, _) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            catalog.Add(record, new Reference(unit.PageId, unit.SlotIndex, writer));
        }
        return catalog;
    }

    /// <summary>Finds a container visible through a snapshot.</summary>
    /// <param name="name">The case-sensitive container name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible container, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public BlobContainerMetadata? FindContainer(string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindContainerVersion(name, snapshot)?.Record.Container;
        }
    }

    /// <summary>Lists containers visible through a snapshot, ordered by ordinal name.</summary>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible container metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public IReadOnlyList<BlobContainerMetadata> GetContainers(TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<BlobContainerMetadata>();
            foreach (string name in _containers.Keys.Order(StringComparer.Ordinal).ToArray())
            {
                if (FindContainerVersion(name, snapshot) is { } found)
                {
                    result.Add(found.Record.Container!.Value);
                }
            }
            return result;
        }
    }

    /// <summary>Creates or replaces a container metadata version in the supplied transaction.</summary>
    /// <param name="container">The complete container metadata.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The container identity or name is invalid, or a schema-owned container names no owning schema.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public async ValueTask SaveContainerAsync(BlobContainerMetadata container, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(container.Name);
        if (container.Id == Guid.Empty)
        {
            throw new ArgumentException("A container must have a nonempty identity.", nameof(container));
        }
        if (container.Owner is not (DatabaseObjectOwner.Adhoc or DatabaseObjectOwner.Schema)
            || (container.Owner == DatabaseObjectOwner.Schema && string.IsNullOrWhiteSpace(container.OwningSchema)))
        {
            throw new ArgumentException("A schema-owned container must name its owning schema.", nameof(container));
        }

        Found? previous;
        lock (_sync)
        {
            previous = FindContainerVersion(container.Name, context.Snapshot);
        }
        await SaveAsync(new CatalogRecord(container, null), previous, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tombstones a container metadata version in the supplied transaction.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller handles ownership enforcement and deletion of contained blobs.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public async ValueTask DeleteContainerAsync(Guid containerId, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Found? previous = null;
        lock (_sync)
        {
            foreach (string name in _containers.Keys.ToArray())
            {
                var found = FindContainerVersion(name, context.Snapshot);
                if (found?.Record.Container?.Id == containerId)
                {
                    previous = found;
                    break;
                }
            }
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds a blob metadata version visible through a snapshot.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="name">The case-sensitive blob name.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible blob metadata, or null.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="snapshot"/> is null.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public BlobCatalogEntry? FindBlob(Guid containerId, string name, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            return FindBlobVersion(containerId, name, snapshot)?.Record.Blob;
        }
    }

    /// <summary>Lists visible blob metadata, optionally filtered by an ordinal name prefix.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="prefix">The case-sensitive name prefix, or null for all names.</param>
    /// <param name="snapshot">The reader's visibility snapshot.</param>
    /// <returns>The visible blob metadata ordered by ordinal name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public IReadOnlyList<BlobCatalogEntry> GetBlobs(Guid containerId, string? prefix, TransactionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_sync)
        {
            var result = new List<BlobCatalogEntry>();
            foreach (var key in _blobs.Keys
                .Where(key => key.ContainerId == containerId && (prefix is null || key.Name.StartsWith(prefix, StringComparison.Ordinal)))
                .OrderBy(key => key.Name, StringComparer.Ordinal).ToArray())
            {
                if (FindBlobVersion(containerId, key.Name, snapshot) is { } found)
                {
                    result.Add(found.Record.Blob!.Value);
                }
            }
            return result;
        }
    }

    /// <summary>Creates or replaces blob metadata in the supplied transaction.</summary>
    /// <param name="blob">The complete blob metadata and chunk head reference.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The blob identity, name, or length is invalid.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public async ValueTask SaveBlobAsync(BlobCatalogEntry blob, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(blob.Name);
        if (blob.ContainerId == Guid.Empty || blob.Length < 0 || ((blob.Length == 0) != (blob.HeadLocation == 0)))
        {
            throw new ArgumentException("Blob metadata requires a container identity and a head for nonempty content.", nameof(blob));
        }

        Found? previous;
        lock (_sync)
        {
            previous = FindBlobVersion(blob.ContainerId, blob.Name, context.Snapshot);
        }
        await SaveAsync(new CatalogRecord(null, blob), previous, context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tombstones blob metadata in the supplied transaction.</summary>
    /// <param name="containerId">The stable container identity.</param>
    /// <param name="name">The case-sensitive blob name.</param>
    /// <param name="context">The logical transaction owning the mutation.</param>
    /// <param name="cancellationToken">Cancels the operation before physical application.</param>
    /// <returns>A task representing the operation.</returns>
    /// <remarks>The caller tombstones the content chain in the same logical transaction.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="context"/> is not active.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the physical application.</exception>
    /// <exception cref="TransactionAbortedException">The transaction ended, or its end began, before the mutation was applied.</exception>
    /// <exception cref="BlobCatalogException">A persisted catalog record is malformed.</exception>
    public async ValueTask DeleteBlobAsync(Guid containerId, string name, TransactionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(context);
        Found? previous;
        lock (_sync)
        {
            previous = FindBlobVersion(containerId, name, context.Snapshot);
        }
        await DeleteAsync(previous, context, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask SaveAsync(CatalogRecord record, Found? previous, TransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        byte[] bytes = BlobCatalogCodec.Encode(record, context.Sequence);
        var location = await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            if (previous is { } old)
            {
                Tombstone(bracket, old.Reference, context.Sequence);
            }
            var inserted = _storage.InsertEntry(bracket, bytes);
            _coordinator.VersionStore.RecordCreated(context.Sequence, inserted.PageId, inserted.SlotIndex);
            return inserted;
        }, cancellationToken).ConfigureAwait(false);

        // Publish only after the physical bracket succeeds. Readers re-read stamps
        // so transaction rollback, purge and page reuse cannot stale the directory.
        lock (_sync)
        {
            Add(record, new Reference(location.PageId, location.SlotIndex, context.Sequence));
        }
    }

    private async ValueTask DeleteAsync(Found? previous, TransactionContext context, CancellationToken cancellationToken)
    {
        EnsureActive(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (previous is not { } old)
        {
            return;
        }
        await _coordinator.ApplyStatementAsync(context, bracket =>
        {
            Tombstone(bracket, old.Reference, context.Sequence);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Tombstone(StorageTransaction bracket, Reference reference, TransactionSequence writer)
    {
        var bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
        var tombstoned = RecordVersionStamp.WithDeleter(bytes.Span, writer);
        _storage.UpdateEntry(bracket, reference.PageId, reference.SlotIndex, tombstoned);
        _coordinator.VersionStore.RecordTombstoned(writer, reference.PageId, reference.SlotIndex);
    }

    private void Add(CatalogRecord record, Reference reference)
    {
        List<Reference>? versions;
        if (record.Container is { } container)
        {
            if (!_containers.TryGetValue(container.Name, out versions))
            {
                _containers[container.Name] = versions = new List<Reference>();
            }
        }
        else
        {
            var blob = record.Blob!.Value;
            var key = (blob.ContainerId, blob.Name);
            if (!_blobs.TryGetValue(key, out versions))
            {
                _blobs[key] = versions = new List<Reference>();
            }
        }
        versions.RemoveAll(item => item.PageId == reference.PageId && item.SlotIndex == reference.SlotIndex);
        versions.Add(reference);
    }

    private Found? FindContainerVersion(string name, TransactionSnapshot snapshot)
    {
        if (!_containers.TryGetValue(name, out var versions))
        {
            return null;
        }
        var result = Find(versions, snapshot, record => record.Container?.Name == name);
        if (versions.Count == 0)
        {
            _containers.Remove(name);
        }
        return result;
    }

    private Found? FindBlobVersion(Guid containerId, string name, TransactionSnapshot snapshot)
    {
        if (!_blobs.TryGetValue((containerId, name), out var versions))
        {
            return null;
        }
        var result = Find(versions, snapshot, record => record.Blob is { } blob && blob.ContainerId == containerId && blob.Name == name);
        if (versions.Count == 0)
        {
            _blobs.Remove((containerId, name));
        }
        return result;
    }

    private Found? Find(List<Reference> versions, TransactionSnapshot snapshot, Func<CatalogRecord, bool> matches)
    {
        Found? result = null;
        for (int i = versions.Count - 1; i >= 0; i--)
        {
            var reference = versions[i];
            ReadOnlyMemory<byte> bytes;
            try
            {
                bytes = _storage.ReadEntry(reference.PageId, reference.SlotIndex);
            }
            catch (SlottedPageException)
            {
                versions.RemoveAt(i);
                continue;
            }
            catch (ArgumentOutOfRangeException)
            {
                versions.RemoveAt(i);
                continue;
            }
            // CRC and I/O exceptions deliberately propagate. Only reclaimed slots
            // and changed identities invalidate a cached physical reference.
            if (bytes.Length < RecordVersionStamp.HeaderSize + 2)
            {
                throw new BlobCatalogException("Truncated blob catalog record.");
            }
            var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
            if (writer != reference.Writer)
            {
                versions.RemoveAt(i);
                continue;
            }
            var record = BlobCatalogCodec.Decode(bytes.Span);
            if (!matches(record))
            {
                versions.RemoveAt(i);
                continue;
            }
            if (snapshot.IsVisible(writer) && (deleter == TransactionSequence.None || !snapshot.IsVisible(deleter))
                && (result is null || writer > result.Value.Reference.Writer))
            {
                result = new Found(reference, record);
            }
        }
        return result;
    }

    private static void EnsureActive(TransactionContext context)
    {
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("Catalog mutations require an active transaction.");
        }
    }

    private readonly record struct Reference(PageId PageId, int SlotIndex, TransactionSequence Writer);
    private readonly record struct Found(Reference Reference, CatalogRecord Record);
}
