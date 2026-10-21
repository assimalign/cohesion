using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Blob.Internal;
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
public sealed partial class BlobCatalog
{
    private readonly BlobStorage _storage;
    private readonly TransactionCoordinator _coordinator;
    private readonly object _sync = new();
    private long _recordReadCount;
    private readonly LinkedList<Versions> _maintenance = new();
    private LinkedListNode<Versions>? _maintenanceCursor;

    private readonly Dictionary<string, Versions> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid ContainerId, string Name), Versions> _blobs = new();

    // Deterministic lookup-work observability for this assembly's tests.
    internal long RecordReadCount { get { lock (_sync) { return _recordReadCount; } } }
    internal int CachedReferenceCount { get { lock (_sync) { return _containers.Values.Concat(_blobs.Values).Sum(versions => versions.Count); } } }

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
            var (writer, deleter) = RecordVersionStamp.ReadStamps(unit.Data.Span);
            catalog.Add(record, new Reference(unit.PageId, unit.SlotIndex, writer), deleter);
        }
        foreach (var versions in catalog._maintenance) { versions.RebuildPublicationOrder(); }
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

    private void Add(CatalogRecord record, Reference reference, TransactionSequence? recoveredDeleter = null)
    {
        Versions? versions;
        if (record.Container is { } container)
        {
            if (!_containers.TryGetValue(container.Name, out versions))
            {
                _containers[container.Name] = versions = new Versions();
                Track(versions, value => value.Container?.Name == container.Name, () => _containers.Remove(container.Name));
            }
        }
        else
        {
            var blob = record.Blob!.Value;
            var key = (blob.ContainerId, blob.Name);
            if (!_blobs.TryGetValue(key, out versions))
            {
                _blobs[key] = versions = new Versions();
                Track(versions, value => value.Blob is { } blob && blob.ContainerId == key.ContainerId && blob.Name == key.Name, () => _blobs.Remove(key));
            }
        }
        versions.Add(reference, recoveredDeleter);
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
            Retire(versions);
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
            Retire(versions);
        }
        return result;
    }

    private Found? Find(Versions versions, TransactionSnapshot snapshot, Func<CatalogRecord, bool> matches)
    {
        // Lock order: model writer lock, catalog monitor, storage page latches.
        // Apply/undo never enter this monitor. Maintenance runs after the purge gate exits.
        long before = _recordReadCount;
        var result = Select(versions, snapshot, matches);
        // Three reads suffice for a current lookup, including cleanup and an undone head.
        int probes = (int)Math.Min(versions.Count, Math.Max(0L, 3 - (_recordReadCount - before)));
        for (int i = 0; i < probes; i++)
        {
            var candidate = versions.NextSweep()!;
            if (Read(candidate, matches) is null) { versions.Remove(candidate); }
        }
        return result;
    }

    private Found? Select(Versions versions, TransactionSnapshot snapshot, Func<CatalogRecord, bool> matches)
    {
        Found? best = null;
        var head = versions.Current;
        bool known = versions.TerminalKnown;
        for (var reference = head; reference is not null;)
        {
            var previous = reference.Previous;
            bool predecessorKnown = known && reference.PredecessorKnown;
            // Query existing coordinator state by sequence; no directory reference owns a
            // transaction or its snapshot. Deferred undo remains pending to every reader.
            if (IsAborted(reference.Writer))
            {
                versions.Remove(reference);
                known = predecessorKnown;
                reference = previous;
                continue;
            }
            var record = Read(reference, matches);
            if (record is null)
            {
                versions.Remove(reference);
                if (known && !ReferenceEquals(reference, head))
                {
                    // This is the committed predecessor, not an aborted newest publication.
                    // Its reclamation proves every older publication is below the purge
                    // horizon too. A recreated head may still be pending above this suffix.
                    if (versions.Current is null || ReferenceEquals(versions.Current, previous))
                    {
                        versions.Discard();
                    }
                    return null;
                }
            }
            else if (snapshot.IsVisible(reference.Writer))
            {
                if (record.Value.Deleter == TransactionSequence.None || !snapshot.IsVisible(record.Value.Deleter))
                {
                    var found = new Found(reference, record.Value.Record);
                    if (known) { return found; }
                    if (best is null || reference.Writer > best.Value.Reference.Writer) { best = found; }
                }
                else if (known) { return null; }
            }
            known = predecessorKnown;
            reference = previous;
        }
        return best;
    }

    private bool IsAborted(TransactionSequence writer)
    {
        if (_coordinator.VersionStore.PendingAbortedPurges.Contains(writer.Value)) { return true; }
        foreach (var context in _coordinator.GetOpenContexts())
        {
            if (context.Sequence == writer)
            {
                return context.State is TransactionState.RolledBack or TransactionState.Faulted;
            }
        }
        return false;
    }

    private void Track(Versions versions, Func<CatalogRecord, bool> matches, Action retire)
    {
        versions.Matches = matches;
        versions.Retire = retire;
        versions.MaintenanceNode = _maintenance.AddLast(versions);
    }

    private void Retire(Versions versions)
    {
        var node = versions.MaintenanceNode!;
        if (ReferenceEquals(_maintenanceCursor, node)) { _maintenanceCursor = node.Next; }
        _maintenance.Remove(node);
        versions.MaintenanceNode = null;
        versions.Retire();
    }

    // Test-only hook at the call site: no shipped assembly boundary is widened.
    internal Action? BeforeHistoryMaintenance { get; set; }

    // Model-local pass: the worker and deterministic catalog tests use the same boundary.
    internal long RunVersionPurgePass(CancellationToken cancellationToken)
    {
        Exception? purgeFailure = null;
        bool purgeCompleted = false;
        try
        {
            long total = _coordinator.RunVersionPurgePass(cancellationToken);
            purgeCompleted = true;
            return total;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            purgeFailure = exception;
            throw;
        }
        finally
        {
            // An uncaught fatal failure must escape without allocating or running more maintenance.
            if (purgeCompleted || purgeFailure is not null)
            {
                try { MaintainPurgedReferences(); }
                catch (Exception maintenanceFailure) when (purgeFailure is not null && maintenanceFailure is not OutOfMemoryException)
                {
                    // Preserve both faults in execution order; cleanup must not hide the purge failure.
                    throw new AggregateException(purgeFailure, maintenanceFailure);
                }
            }
        }
    }

    internal void MaintainPurgedReferences()
    {
        BeforeHistoryMaintenance?.Invoke();
        lock (_sync)
        {
            // One reference per key per turn, round-robin: at most 128 record reads per
            // pass, independent of name count and historical peak. An idle name needs
            // no subsequent lookup to relinquish its purged versions.
            for (int probe = 0; probe < 128 && _maintenance.Count != 0; probe++)
            {
                var node = _maintenanceCursor ?? _maintenance.First!;
                _maintenanceCursor = node.Next;
                var versions = node.Value;
                var reference = versions.NextSweep();
                if (reference is not null && Read(reference, versions.Matches) is null)
                {
                    versions.Remove(reference);
                }
                if (versions.Count == 0) { Retire(versions); }
            }
        }
    }

    private (CatalogRecord Record, TransactionSequence Deleter)? Read(Reference reference, Func<CatalogRecord, bool> matches)
    {
        // Reclaimed slots/pages and changed identities are absence. CRC, I/O and live
        // decoding failures propagate, including during the bounded cleanup probes.
        _recordReadCount++;
        if (!_storage.TryReadRecord(reference.PageId, reference.SlotIndex, 0, out var bytes)) { return null; }
        if (bytes.Length < RecordVersionStamp.HeaderSize + 2)
        {
            throw new BlobCatalogException("Truncated blob catalog record.");
        }
        var (writer, deleter) = RecordVersionStamp.ReadStamps(bytes.Span);
        if (writer != reference.Writer) { return null; }
        var record = BlobCatalogCodec.Decode(bytes.Span);
        return matches(record) ? (record, deleter) : null;
    }

    private static void EnsureActive(TransactionContext context)
    {
        if (context.State != TransactionState.Active)
        {
            throw new InvalidOperationException("Catalog mutations require an active transaction.");
        }
    }

    private readonly record struct Found(Reference Reference, CatalogRecord Record);
}
