using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    internal static Action? ReplacementMarkerExamined { get; set; }

    private readonly Dictionary<ulong, (ulong ReplacementObjectId, TransactionSequence Marker, SqlCatalogSnapshot Before)> _truncatedTables = new();
    private (ulong ObjectId, ulong ReplacementObjectId, TransactionSequence Marker)[] _orderedTruncations = [];
    private readonly Dictionary<SqlCatalogIndex, BTreeIndex> _truncatedIndexes = new(ReferenceEqualityComparer.Instance);
    // Unreachable build owners for TRUNCATE/rewrite replacements and CTAS alike.
    private readonly HashSet<ulong> _preparedTruncateOwners = new();
    private readonly HashSet<ulong> _truncateRetirementsPublishing = new();
    private ulong _storageBindingGeneration;

    // Physical replacement invalidates table/view bindings without reporting logical schema
    // drift to compiled-schema and declared-script verification. Reopen has no cached plans.
    internal (ulong Definition, ulong Storage) BindingGeneration
    {
        get
        {
            ThrowIfOffline();
            // Both components only increase. A mixed observation cannot hide an
            // intervening change at the planner's final comparison. Metadata reads
            // still take _sync; generation-only checks need no reservation flush.
            return (Volatile.Read(ref _ddlGeneration), Volatile.Read(ref _storageBindingGeneration));
        }
    }

    // BEGIN holds this only across creation of its MVCC snapshot and catalog capture;
    // TRUNCATE holds it only across final publication. BEGIN takes no table locks, so
    // table-X -> this gate -> catalog lock cannot form a cycle with snapshot admission.
    // No AvailableWaitHandle is used; like the DDL admission semaphores it owns no OS handle.
    internal SemaphoreSlim TruncateSnapshotGate { get; } = new(1, 1);

    // The short-lived marker is active before publication and ended just after it. A
    // snapshot that cannot see it needs the retired owner, even if binding starts later.
    // This covers BEGIN, auto-commit and ReadCommitted without catalog capture on their
    // normal stored-query path. No snapshot survives reopen, so the association is volatile.
    internal SqlCatalogSnapshot? CaptureTruncateReadSnapshot(TransactionSnapshot snapshot)
    {
        var retirements = Volatile.Read(ref _orderedTruncations);
        // Publication markers are serialized by TruncateSnapshotGate. Seeing the maximum
        // therefore implies seeing every earlier marker, even with older readers pinned.
        if (retirements.Length == 0 || snapshot.IsVisible(retirements[^1].Marker)) { return null; }
        return CaptureRetiredReadSnapshot(snapshot);
    }

    // Keep captured predicates out of the common no-retirement check. Adding a
    // visibility filter must not allocate a closure for every ordinary statement.
    private SqlCatalogSnapshot? CaptureRetiredReadSnapshot(TransactionSnapshot snapshot)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            var tables = new Dictionary<(string Schema, string Name), SqlCatalogTable>(TableNameComparer.Instance);
            var indexes = new List<SqlCatalogIndex>();
            var views = new Dictionary<string, SqlCatalogView>(StringComparer.OrdinalIgnoreCase);
            var sequences = new Dictionary<ulong, SqlCatalogSequence>();
            foreach (var retired in _orderedTruncations)
            {
                if (snapshot.IsVisible(retired.Marker)) { continue; }
                var before = _truncatedTables[retired.ObjectId].Before;
                var table = before.Tables.Single(table => table.ObjectId == retired.ObjectId);
                // Multiple truncations can overlap one reader: keep the first owner it
                // predates, rather than a later empty replacement of the same table.
                if (!tables.TryAdd((table.Schema, table.Name), table)) { continue; }
                indexes.AddRange(before.GetIndexes(table.ObjectId));
                foreach (var sequence in before.Sequences.Where(sequence => sequence.OwnerTableId == table.ObjectId)) { sequences.TryAdd(sequence.ObjectId, sequence); }
                foreach (var view in before.Views.Where(view => view.TableDependencies.Contains(table.ObjectId)))
                {
                    views.TryAdd(SqlObjectIdentity.View(view.Schema, view.Name), view);
                }
            }
            if (tables.Count == 0) { return null; }
            return new SqlCatalogSnapshot(_tables.Values.Select(slot => slot.Table)
                    .Where(table => !tables.ContainsKey((table.Schema, table.Name))).Concat(tables.Values),
                _indexes.Values.Select(slot => slot.Index).Concat(indexes),
                _defaultCollation, _functions.Values.Select(slot => slot.Value), _domains.Values.Select(slot => slot.Value),
                _namespaces.Values.Select(slot => slot.Value),
                _views.Where(pair => !views.ContainsKey(pair.Key)).Select(pair => pair.Value.Value).Concat(views.Values))
                { Sequences = Array.AsReadOnly(_sequencesById.Values.Where(sequence =>
                    (sequence.OwnerTableId == 0 || !_pendingReclamations.ContainsKey(sequence.OwnerTableId)) &&
                    !sequences.ContainsKey(sequence.ObjectId)).Concat(sequences.Values).ToArray()) };
        }
    }

    // Ordinary FK probes allocate nothing. The statement's pin retains these immutable
    // publication markers until its snapshot and all its constraint reads have ended.
    internal bool SnapshotPredatesReplacement(SqlCatalogTable current, TransactionSnapshot snapshot)
    {
        var retirements = Volatile.Read(ref _orderedTruncations);
        if (retirements.Length == 0) { return false; }
        // Publication serializes markers. Seeing the newest implies seeing all of
        // them, even when an older reader keeps arbitrarily many owners retained.
        ReplacementMarkerExamined?.Invoke();
        if (snapshot.IsVisible(retirements[^1].Marker)) { return false; }
        for (int index = 0; index < retirements.Length; index++)
        {
            var retirement = retirements[index];
            ReplacementMarkerExamined?.Invoke();
            if (snapshot.IsVisible(retirement.Marker)) { continue; }
            // Rename preserves this identity and updates incoming FK names. Names
            // cannot identify the publication whose compact owner a probe will read.
            if (retirement.ReplacementObjectId == current.ObjectId) { return true; }
        }
        return false;
    }

    // Internal engine operations: no new cross-assembly catalog seam. Callers hold table X
    // locks in ascending identity order, then enter this metadata lock. No data apply gate
    // or tree/page latch is held here. Catalog storage brackets are strictly innermost.
    internal SqlCatalogTable[] ReserveStorageReplacement(IReadOnlyList<SqlCatalogTable> expected,
        IReadOnlyList<SqlCatalogTable>? named, Func<SqlCatalogTable, ulong, SqlCatalogTable>? describe, CancellationToken cancellationToken,
        bool newTable = false)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (!newTable) { ValidateStorageReplacement(expected, named); }
            else if (expected.Any(table => RelationExists(table.Schema, table.Name, includeOrphans: true)))
            { throw new SqlCatalogException("CS04064", ErrorMessages.CatalogSaveSchemaDocumentSqlCatalog); }
            ulong next = _nextObjectId;
            var replacements = expected.Select(table => describe is not null ? describe(table, checked(next++)) : new SqlCatalogTable(checked(next++), table.Schema, table.Name,
                table.Columns, table.PrimaryKeyColumns, table.Owner, table.OwningSchema, table.Constraints,
                table.DroppedColumnOrdinals)).ToArray();
            using var transaction = _storage.BeginTransaction();
            // Reuse kind 14 for unreachable prepared owners too. A failed build or crash
            // leaves durable reclamation work. The active statement pins these owners until
            // publication/cleanup has finished; successful publication removes these intents.
            var locations = replacements.Select(table => _storage.InsertRow(transaction, table.ObjectId,
                SqlReclamationRecord.Encode(table.ObjectId, table.Schema, table.Name))).ToArray();
            Action[] owned = describe is null ? [] : replacements.Select((table, index) =>
            {
                var additions = table.Columns.Where(column => column.Identity is not null && expected[index].FindColumn(column.Name)?.Identity is null).ToArray();
                if (additions.Length == 0) { return (Action)(() => { }); }
                return StageOwnedSequences(transaction, null, new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, additions), ref next);
            }).ToArray();
            var counterLocation = PersistCounter(transaction, next, _ddlGeneration);
            var intents = locations.Select(location => new SqlReclamationIntent(location.PageId, location.SlotIndex)).ToArray();
            _pendingReclamations.EnsureCapacity(_pendingReclamations.Count + replacements.Length);
            _preparedTruncateOwners.EnsureCapacity(_preparedTruncateOwners.Count + replacements.Length);
            transaction.Commit();
            foreach (var publish in owned) { publish(); }
            _counterLocation = counterLocation;
            _nextObjectId = next;
            for (int index = 0; index < replacements.Length; index++)
            {
                _pendingReclamations.Add(replacements[index].ObjectId, intents[index]);
                _preparedTruncateOwners.Add(replacements[index].ObjectId);
            }
            return replacements;
        }
    }

    // Once preparation exits it cannot publish. Its durable intents remain visible
    // until reclamation, including when mandatory cleanup encounters offline storage.
    internal void AbandonStorageReplacement(IReadOnlyList<SqlCatalogTable> replacements)
    {
        lock (_sync)
        {
            for (int index = 0; index < replacements.Count; index++)
            { _preparedTruncateOwners.Remove(replacements[index].ObjectId); }
        }
    }

    internal ValueTask PublishStorageReplacementAsync(IReadOnlyList<SqlCatalogTable> expected, IReadOnlyList<SqlCatalogTable>? named,
        IReadOnlyList<SqlCatalogTable> replacements, IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations, Dictionary<SqlCatalogIndex, BTreeIndex> retiredIndexes,
        bool restartIdentity, TransactionSequence marker, string actualObject, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken,
        bool definitionChange = false, SqlCatalogSchemaState? appliedSchema = null,
        IReadOnlyList<SqlCatalogTable>? dropped = null)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStorageReplacement(expected, named);
            dropped ??= [];
            ValidateStorageReplacement(dropped, null);
            foreach (var table in replacements)
            {
                // Use the same namespace as Load's view/sequence directory validators.
                // Existing tables may keep their own name; every other relation must be absent.
                if (_views.ContainsKey(SqlObjectIdentity.View(table.Schema, table.Name)) ||
                    _sequences.ContainsKey((table.Schema, table.Name)) ||
                    _tables.ContainsKey((table.Schema, table.Name)) &&
                    !expected.Any(old => TableNameComparer.Instance.Equals((old.Schema, old.Name), (table.Schema, table.Name))))
                { throw new SqlCatalogException("CS04064", ErrorMessages.CatalogSaveSchemaDocumentSqlCatalog); }
            }
            var retiring = expected.Concat(dropped).ToArray();
            ulong bindingGeneration = checked(_storageBindingGeneration + 1);
            var ids = retiring.Select(table => table.ObjectId).ToHashSet();
            var mapping = expected.Select((table, index) => (table.ObjectId, Replacement: replacements[index]))
                .ToDictionary(pair => pair.ObjectId, pair => pair.Replacement);
            var merged = MergePublishedRegistrations(registrations, indexes)
                .Where(registration => !ids.Contains(registration.ObjectId)).ToArray();
            var columns = expected.SelectMany(table => table.Columns.Select(column =>
                (Old: SqlObjectIdentity.Column(table.ObjectId, column.Name),
                 New: SqlObjectIdentity.Column(mapping[table.ObjectId].ObjectId, column.Name))))
                .ToDictionary(pair => pair.Old, pair => pair.New, StringComparer.OrdinalIgnoreCase);
            var views = _views.Where(pair => pair.Value.Value.TableDependencies.Any(ids.Contains) ||
                pair.Value.Value.Dependencies.Any(columns.ContainsKey)).Select(pair =>
            {
                var view = pair.Value.Value;
                var replacement = new SqlCatalogView(view.Schema, view.Name, view.Definition, view.Columns,
                    view.TableDependencies.Select(id => mapping.TryGetValue(id, out var table) ? table.ObjectId : id).ToArray(),
                    view.Dependencies.Select(key => columns.GetValueOrDefault(key, key)).ToArray(), view.FunctionCalls,
                    view.Owner, view.OwningSchema);
                var record = new DatabaseKeyWriter().AppendInt32(viewRecordKind)
                    .AppendString(JsonSerializer.Serialize(SqlViewRecord.From(replacement), SqlCatalogJsonContext.Default.SqlViewRecord), Collation.Binary).ToArray();
                if (record.Length > SlottedPage.MaxRecordSize) { throw new SqlCatalogException("CS03033", ErrorMessages.ViewRecordTooLarge); }
                return (pair.Key, Old: pair.Value, View: replacement, Record: record);
            }).ToArray();
            var before = new SqlCatalogSnapshot(retiring, _indexes.Values.Select(slot => slot.Index).Where(index => ids.Contains(index.TableObjectId)),
                views: _views.Values.Select(slot => slot.Value).Where(view => view.TableDependencies.Any(ids.Contains)))
                { Sequences = _sequences.Count == 0 ? [] : Array.AsReadOnly(_sequencesById.Values.Where(sequence => ids.Contains(sequence.OwnerTableId)).ToArray()) };
            var tableRecords = replacements.Select(EncodeTable).ToArray();
            var indexRecords = indexes.Select(EncodeIndex).ToArray();
            using var transaction = _storage.BeginTransaction();
            var tableSlots = replacements.Select((table, index) => new TableSlot(table,
                UpsertRecord(transaction, _tables.TryGetValue((table.Schema, table.Name), out var existing) ? existing.Location : null, tableRecords[index]))).ToArray();
            foreach (var table in dropped)
            {
                var location = _tables[(table.Schema, table.Name)].Location;
                _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
            }
            var indexSlots = indexes.Select((index, ordinal) => new IndexSlot(index,
                _storage.InsertRow(transaction, indexRecords[ordinal]))).ToArray();
            var viewSlots = views.Select(view => (view.Key, Slot: new DefinitionSlot<SqlCatalogView>(view.View,
                UpsertRecord(transaction, view.Old.Location, view.Record)))).ToArray();
            var oldIndexes = _indexes.Values.Where(slot => ids.Contains(slot.Index.TableObjectId)).ToArray();
            foreach (var slot in oldIndexes) { _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex); }
            var retired = retiring.Select(table => (table.ObjectId, Location: _storage.InsertRow(transaction, table.ObjectId,
                SqlReclamationRecord.Encode(table.ObjectId, table.Schema, table.Name)))).ToArray();
            ulong next = _nextObjectId;
            Action[] owned = _sequences.Count == 0 ? [] : retiring.Select(table => StageOwnedSequences(transaction, table, mapping.GetValueOrDefault(table.ObjectId), ref next, restartIdentity)).ToArray();
            foreach (var table in replacements)
            {
                var location = _pendingReclamations[table.ObjectId];
                _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
            }
            var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(merged));
            var stateLocations = appliedSchema is null ? null : StageJointSchemaState(transaction, appliedSchema);
            var appliedState = appliedSchema is null ? null : new SqlCatalogSchemaState(appliedSchema.ContentHash,
                appliedSchema.CanonicalDocument, checked(_ddlGeneration + 1));
            // Allocate every publication directory/vector before the irrevocable commit.
            // The monitor excludes readers; the post-commit section only assigns/removes
            // preconstructed entries, including during memory pressure.
            var planningIndexes = indexes.GroupBy(index => index.TableObjectId).Select(group =>
                (Id: group.Key, Indexes: group.OrderBy(index => index.Name, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
            var retirements = retired.Select(item => new SqlReclamationIntent(item.Location.PageId, item.Location.SlotIndex)).ToArray();
            var ordered = new (ulong ObjectId, ulong ReplacementObjectId, TransactionSequence Marker)[_orderedTruncations.Length + retiring.Length];
            _orderedTruncations.CopyTo(ordered, 0);
            for (int index = 0; index < retiring.Length; index++)
            { ordered[_orderedTruncations.Length + index] = (retiring[index].ObjectId, mapping.GetValueOrDefault(retiring[index].ObjectId)?.ObjectId ?? 0, marker); }
            _tables.EnsureCapacity(_tables.Count + replacements.Count);
            _indexes.EnsureCapacity(_indexes.Count + indexes.Count);
            _planningIndexes.EnsureCapacity(_planningIndexes.Count + planningIndexes.Length);
            _pendingReclamations.EnsureCapacity(_pendingReclamations.Count + retiring.Length);
            _truncateRetirementsPublishing.EnsureCapacity(_truncateRetirementsPublishing.Count + retiring.Length);
            _truncatedTables.EnsureCapacity(_truncatedTables.Count + retiring.Length);
            _truncatedIndexes.EnsureCapacity(_truncatedIndexes.Count + retiredIndexes.Count);
            _identitySequences.EnsureCapacity(_identitySequences.Count + replacements.Count);
            DefinitionSlot<SqlCatalogScript>? receipt = null;
            if (!definitionChange && publication is not null)
            {
                ValidateScriptPublication(publication, actualObject);
                receipt = StageScript(transaction, CreateScriptReceipt(publication, _ddlGeneration, actualObject, false, false));
            }
            if (definitionChange) { CommitCatalogChange(transaction, publication: publication, actualObject: actualObject); }
            else { transaction.Commit(); }
            if (receipt is not null) { _scripts[receipt.Value.Name] = receipt; }
            foreach (var slot in tableSlots) { _tables[(slot.Table.Schema, slot.Table.Name)] = slot; }
            for (int index = 0; index < dropped.Count; index++)
            { _tables.Remove((dropped[index].Schema, dropped[index].Name)); }
            if (appliedSchema is not null)
            {
                _schemaState = appliedState;
                _schemaStateLocations = stateLocations!;
                _pendingMigration = null;
                _pendingMigrationLocations = [];
            }
            foreach (var slot in oldIndexes) { _indexes.Remove((slot.Index.TableObjectId, slot.Index.Name)); }
            foreach (var table in retiring) { _planningIndexes.Remove(table.ObjectId); }
            foreach (var slot in indexSlots) { _indexes[(slot.Index.TableObjectId, slot.Index.Name)] = slot; }
            foreach (var item in planningIndexes) { _planningIndexes[item.Id] = item.Indexes; }
            foreach (var view in viewSlots) { _views[view.Key] = view.Slot; }
            foreach (var publish in owned) { publish(); }
            for (int index = 0; index < replacements.Count; index++)
            {
                var table = replacements[index];
                _pendingReclamations.Remove(table.ObjectId);
                _preparedTruncateOwners.Remove(table.ObjectId);
            }
            for (int index = 0; index < retired.Length; index++)
            {
                _pendingReclamations.Add(retired[index].ObjectId, retirements[index]);
                _truncateRetirementsPublishing.Add(retired[index].ObjectId);
            }
            foreach (var table in retiring) { _truncatedTables.Add(table.ObjectId, (mapping.GetValueOrDefault(table.ObjectId)?.ObjectId ?? 0, marker, before)); }
            foreach (var pair in retiredIndexes) { _truncatedIndexes.Add(pair.Key, pair.Value); }
            Volatile.Write(ref _orderedTruncations, ordered);
            _registrations = merged;
            _registrationsLocation = registrationsLocation;
            Volatile.Write(ref _storageBindingGeneration, bindingGeneration);
            _snapshot = null;
            return default;
        }
    }

    // Immutable owner descriptions and detached trees are usable only while reclamation
    // still retains that exact owner. Statement/transaction pins protect the caller's use.
    internal bool IsTruncatedTable(SqlCatalogTable table)
    {
        if (Volatile.Read(ref _orderedTruncations).Length == 0) { return false; }
        lock (_sync)
        {
            ThrowIfOffline();
            return IsTruncatedTableCore(table);
        }
    }

    private bool IsTruncatedTableCore(SqlCatalogTable table)
    {
        if (!_truncatedTables.TryGetValue(table.ObjectId, out var retired)) { return false; }
        for (int index = 0; index < retired.Before.Tables.Count; index++)
        {
            if (ReferenceEquals(retired.Before.Tables[index], table)) { return true; }
        }
        return false;
    }

    internal bool TryGetTruncatedIndex(SqlCatalogTable table, SqlCatalogIndex planned, out BTreeIndex index)
    {
        index = null!;
        if (Volatile.Read(ref _orderedTruncations).Length == 0 || planned.TableObjectId != table.ObjectId) { return false; }
        lock (_sync)
        {
            ThrowIfOffline();
            return IsTruncatedTableCore(table) &&
                _truncatedIndexes.TryGetValue(planned, out index!);
        }
    }

    private void AppendOrderedTruncations(IReadOnlyList<SqlCatalogTable> retired, IReadOnlyList<SqlCatalogTable> replacements,
        TransactionSequence marker)
    {
        // Snapshot admission serializes monotonically increasing markers. The caller
        // supplies this publication's owners in ascending identity order.
        var previous = _orderedTruncations;
        // Snapshot descriptions already live in the retired-owner directory under
        // _sync. Keep this lock-free visibility vector to three scalar identities.
        var next = new (ulong ObjectId, ulong ReplacementObjectId, TransactionSequence Marker)[previous.Length + retired.Count];
        Array.Copy(previous, next, previous.Length);
        for (int index = 0; index < retired.Count; index++)
        {
            next[previous.Length + index] = (retired[index].ObjectId, replacements[index].ObjectId, marker);
        }
        Volatile.Write(ref _orderedTruncations, next);
    }

    private void RemoveOrderedTruncation(ulong objectId)
    {
        var previous = _orderedTruncations;
        int index = 0;
        while (previous[index].ObjectId != objectId) { index++; }
        var next = new (ulong ObjectId, ulong ReplacementObjectId, TransactionSequence Marker)[previous.Length - 1];
        Array.Copy(previous, next, index);
        Array.Copy(previous, index + 1, next, index, previous.Length - index - 1);
        Volatile.Write(ref _orderedTruncations, next);
    }

    private void ValidateStorageReplacement(IReadOnlyList<SqlCatalogTable> expected, IReadOnlyList<SqlCatalogTable>? named)
    {
        foreach (var table in expected)
        {
            if (!_tables.TryGetValue((table.Schema, table.Name), out var slot) || !ReferenceEquals(slot.Table, table))
            {
                throw new SqlCatalogException("CS04064", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.TableIdentityReplaced, table.Schema, table.Name));
            }
        }
        if (named is null) { return; } // A rewrite preserves contents and needs no FK closure.
        // Recompute from the written targets, not the formerly expanded set. A dropped
        // FK can shrink CASCADE while its child keeps the same owner id during the wait.
        // Both reservation and publication must reject growth AND shrinkage of that set.
        var names = named.Select(table => (table.Schema, table.Name)).ToHashSet(TableNameComparer.Instance);
        bool added;
        do
        {
            added = false;
            foreach (var table in _tables.Values.Select(slot => slot.Table))
            {
                if (names.Contains((table.Schema, table.Name))) { continue; }
                if (table.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    names.Contains((constraint.ReferencedSchema!, constraint.ReferencedTable!))))
                {
                    names.Add((table.Schema, table.Name));
                    added = true;
                }
            }
        } while (added);
        if (names.Count != expected.Count || expected.Any(table => !names.Contains((table.Schema, table.Name))))
        {
            throw new SqlCatalogException("CS04064", ErrorMessages.TruncateClosureChanged);
        }
    }

    // Readers admitted while the marker is active can still need the old owner. A worker
    // must allocate its fence after the marker ends, not merely after the catalog swap.
    internal void ReleaseTruncatePublication(IReadOnlyList<SqlCatalogTable> retired)
    {
        lock (_sync)
        {
            foreach (var table in retired) { _truncateRetirementsPublishing.Remove(table.ObjectId); }
        }
    }

    internal (ulong ObjectId, SqlReclamationIntent Intent)[] CaptureTableReclamations()
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return _pendingReclamations.Where(pair => !_truncateRetirementsPublishing.Contains(pair.Key))
                .Select(pair => (pair.Key, pair.Value)).ToArray();
        }
    }

    internal bool IsCurrentTableReclamation(ulong objectId, SqlReclamationIntent intent)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return !_truncateRetirementsPublishing.Contains(objectId) &&
                _pendingReclamations.TryGetValue(objectId, out var current) && ReferenceEquals(current, intent);
        }
    }
}
