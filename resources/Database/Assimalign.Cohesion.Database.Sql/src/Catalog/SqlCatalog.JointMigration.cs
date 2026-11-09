using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    internal Action? BeforeJointDiscard { get; set; }
    internal Action? IndexReclamationRequested { get; set; }
    internal Action<ulong, bool>? IndexBuildFinished { get; set; }
    internal string EngineName { get; set; } = string.Empty;
    private readonly HashSet<ulong> _buildingIndexes = new();
    internal void BeginIndexBuild(ulong owner) { lock (_sync) { _buildingIndexes.Add(owner); } }
    internal void EndIndexBuild(ulong owner, bool succeeded)
    {
        lock (_sync) { if (_buildingIndexes.Remove(owner)) { IndexBuildFinished?.Invoke(owner, succeeded); } }
    }
    internal Func<BTreeIndexRegistration, ulong>? IndexPageOwner { get; set; }
    internal Func<BTreeIndexRegistration, bool>? IndexOwnerKnown { get; set; }
    private readonly Dictionary<long, ulong> _registrationOwners = new();
    internal ulong PhysicalIndexOwner(BTreeIndexRegistration registration)
    {
        ulong live = IndexPageOwner?.Invoke(registration) ?? registration.ObjectId;
        return live != registration.ObjectId ? live : _registrationOwners.GetValueOrDefault(registration.RootPageId, live);
    }
    internal ulong NextReservedIdentity { get { lock (_sync) { return _nextObjectId; } } }
    internal bool IsOwnerPrepared(ulong owner)
    { lock (_sync) { return _preparedTruncateOwners.Contains(owner); } }
    internal bool IsOwnerRetiring(ulong owner)
    { lock (_sync) { return _pendingReclamations.ContainsKey(owner) && !_preparedTruncateOwners.Contains(owner); } }

    internal void WaitForIndexAdmission(CancellationToken token)
    {
        using var admission = TransactionCheckpointInterlock.ForStorage(_storage)
            .Enter("SqlIndexBuild", cancellationToken: token);
    }
    // Old snapshot descriptions can still read the same physical layout. Weak
    // keys avoid retaining one description per owner-keeping publication forever.
    private readonly ConditionalWeakTable<SqlCatalogTable, object> _keptReadVersions = new();
    private readonly Dictionary<TransactionSequence, ulong[]> _indexRetirementsPublishing = new();

    private SqlCatalogTable[] PrepareKeptReadVersions(IReadOnlyList<SqlCatalogTable> kept)
    {
        if (kept.Count == 0) { return []; }
        var added = kept.Select(table => _tables[(table.Schema, table.Name)].Table)
            .Where(previous => !_keptReadVersions.TryGetValue(previous, out _)).ToArray();
        try
        {
            foreach (var previous in added)
            { _keptReadVersions.Add(previous, new object()); }
            return added;
        }
        catch
        {
            foreach (var previous in added) { _keptReadVersions.Remove(previous); }
            throw;
        }
    }

    private bool IsKeptReadVersion(SqlCatalogTable table)
        => _keptReadVersions.TryGetValue(table, out _) &&
            (_tables.TryGetValue((table.Schema, table.Name), out var live) && live.Table.ObjectId == table.ObjectId ||
                _pendingReclamations.ContainsKey(table.ObjectId));

    internal ulong ReserveJointTreeOwner(SqlCatalogTable table, CancellationToken token)
        => ReserveTreeOwner(table.Schema, table.Name, token);

    internal ulong ReserveIndexTreeOwner(ulong tableId, CancellationToken token)
    {
        lock (_sync)
        {
            var table = FindTableByObjectId(tableId);
            return ReserveTreeOwner(table?.Schema ?? "dbo", table?.Name ?? "index", token);
        }
    }

    private ulong ReserveTreeOwner(string schema, string name, CancellationToken token)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            token.ThrowIfCancellationRequested();
            ulong owner = _nextObjectId;
            using var admission = TransactionCheckpointInterlock.ForStorage(_storage)
                .Enter("SqlCatalog", unchecked((long)owner), token, waitForAdmission: false);
            using var transaction = _storage.BeginTransaction("SqlCatalog", unchecked((long)owner));
            var location = _storage.InsertRow(transaction, owner, SqlReclamationRecord.Encode(owner, schema, name));
            var counter = PersistCounter(transaction, checked(owner + 1), _ddlGeneration);
            var intent = new SqlReclamationIntent(location.PageId, location.SlotIndex);
            _pendingReclamations.EnsureCapacity(_pendingReclamations.Count + 1);
            _preparedTruncateOwners.EnsureCapacity(_preparedTruncateOwners.Count + 1);
            transaction.Commit();
            _pendingReclamations.Add(owner, intent);
            _preparedTruncateOwners.Add(owner);
            _counterLocation = counter;
            _nextObjectId = owner + 1;
            return owner;
        }
    }

    // Clear abandonment intents in the SAME durable publication as the registrations.
    private Action StageTreePublications(StorageTransaction transaction, IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        var owners = registrations.Select(registration => IndexPageOwner?.Invoke(registration) ?? registration.ObjectId)
            .Where(owner => _preparedTruncateOwners.Contains(owner)).Distinct().ToArray();
        foreach (ulong owner in owners)
        {
            var intent = _pendingReclamations[owner];
            _storage.DeleteRow(transaction, intent.PageId, intent.SlotIndex);
        }
        return () =>
        {
            foreach (ulong owner in owners)
            {
                _pendingReclamations.Remove(owner);
                _preparedTruncateOwners.Remove(owner);
                EndIndexBuild(owner, true);
            }
        };
    }

    // Alternate tree owners use kind 14 unchanged. Stage retirement in exactly the
    // same transaction that removes their registration, for every SQL DDL path.
    private Action StageIndexRetirements(StorageTransaction transaction, IEnumerable<BTreeIndexRegistration> removed, int additionalOwners = 0,
        TransactionSequence? publicationMarker = null)
    {
        var owners = removed.Select(registration => (Registration: registration,
            Owner: IndexPageOwner?.Invoke(registration) ?? registration.ObjectId))
            .Where(item => item.Owner != item.Registration.ObjectId).ToArray();
        var intents = owners.Select(item =>
        {
            var table = _tables.Values.Single(slot => slot.Table.ObjectId == item.Registration.ObjectId).Table;
            var location = _storage.InsertRow(transaction, item.Owner, SqlReclamationRecord.Encode(item.Owner, table.Schema, table.Name));
            return (item.Owner, Intent: new SqlReclamationIntent(location.PageId, location.SlotIndex));
        }).ToArray();
        _pendingReclamations.EnsureCapacity(_pendingReclamations.Count + intents.Length + additionalOwners);
        ulong[] hidden = publicationMarker is null ? [] : intents.Select(item => item.Owner).ToArray();
        if (hidden.Length != 0)
        {
            _truncateRetirementsPublishing.EnsureCapacity(_truncateRetirementsPublishing.Count + hidden.Length + additionalOwners);
            _indexRetirementsPublishing.EnsureCapacity(_indexRetirementsPublishing.Count + 1);
        }
        return () =>
        {
            foreach (var item in intents) { _pendingReclamations.Add(item.Owner, item.Intent); }
            if (intents.Length != 0) { IndexReclamationRequested?.Invoke(); }
            if (hidden.Length != 0)
            {
                foreach (ulong owner in hidden) { _truncateRetirementsPublishing.Add(owner); }
                _indexRetirementsPublishing.Add(publicationMarker!.Value, hidden);
            }
        };
    }

    // Called under the catalog monitor, including by implicit identity naming.
    // Compiled tables live in dbo. Kind 9 carries new-table names for the whole job.
    private bool JointNameReserved(string schema, string name)
    {
        if (_pendingMigration is null || !schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)) { return false; }
        foreach (var step in _pendingMigration.Steps)
        {
            if (step.Kind == "Joint/AddTable" && step.Table.Equals(name, StringComparison.OrdinalIgnoreCase)) { return true; }
        }
        return false;
    }

    // Reuse kind 6 and kind 9 in the same storage transaction as SQ-T1's owner swap.
    // No durable envelope or record kind is added by joint migrations.
    private List<(PageId PageId, int SlotIndex)> StageJointSchemaState(StorageTransaction transaction, SqlCatalogSchemaState state)
    {
        foreach (var location in _schemaStateLocations)
        { _storage.DeleteRow(transaction, location.PageId, location.SlotIndex); }
        foreach (var location in _pendingMigrationLocations)
        { _storage.DeleteRow(transaction, location.PageId, location.SlotIndex); }
        byte[] document = _strictUtf8.GetBytes(state.CanonicalDocument);
        int count = Math.Max(1, (document.Length + schemaStateChunkSize - 1) / schemaStateChunkSize);
        var locations = new List<(PageId PageId, int SlotIndex)>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = index * schemaStateChunkSize;
            locations.Add(_storage.InsertRow(transaction, EncodeSchemaStateChunk(state.ContentHash,
                checked(_ddlGeneration + 1), index, count,
                document.AsSpan(offset, Math.Min(schemaStateChunkSize, document.Length - offset)), schemaStateRecordKind)));
        }
        return locations;
    }

    internal ValueTask DiscardJointIntentAsync(CancellationToken cancellationToken,
        IReadOnlyList<SqlCatalogTable>? prepared = null, IReadOnlyList<ulong>? preparedTrees = null, bool drain = false)
    {
        BeforeJointDiscard?.Invoke();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (_pendingMigration is null && prepared is not { Count: > 0 }) { return default; }
            using var checkpointAdmission1 = TransactionCheckpointInterlock.ForStorage(_storage).Enter("SqlCatalog", cancellationToken: cancellationToken, waitForAdmission: false, drain: drain);
            using var transaction = _storage.BeginTransaction("SqlCatalog");
            foreach (var location in _pendingMigrationLocations)
            { _storage.DeleteRow(transaction, location.PageId, location.SlotIndex); }
            transaction.Commit();
            // Each owner already has its durable kind-14 reclamation record. Commit
            // removal of kind 9 before making those owners eligible for reclamation,
            // under the same monitor: no observer can retain a resumable partial job.
            if (prepared is not null)
            {
                for (int index = 0; index < prepared.Count; index++)
                { _preparedTruncateOwners.Remove(prepared[index].ObjectId); }
            }
            if (preparedTrees is not null)
            { foreach (ulong owner in preparedTrees) { _preparedTruncateOwners.Remove(owner); } }
            _pendingMigrationLocations = [];
            _pendingMigration = null;
            return default;
        }
    }

    internal void AbandonJointTreeOwners(IReadOnlyList<ulong> owners)
    {
        lock (_sync) { foreach (ulong owner in owners) { _preparedTruncateOwners.Remove(owner); } }
    }
}
