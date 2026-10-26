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
    private readonly Dictionary<ulong, (TransactionSequence Marker, SqlCatalogSnapshot Before)> _truncatedTables = new();
    private int _truncatedTableCount;
    private ulong _storageBindingGeneration;

    // Physical replacement invalidates table/view bindings without reporting logical schema
    // drift to compiled-schema and declared-script verification. Reopen has no cached plans.
    internal (ulong Definition, ulong Storage) BindingGeneration
    {
        get { lock (_sync) { ThrowIfOffline(); return (_ddlGeneration, _storageBindingGeneration); } }
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
        if (Volatile.Read(ref _truncatedTableCount) == 0) { return null; }
        lock (_sync)
        {
            ThrowIfOffline();
            var tables = new Dictionary<(string Schema, string Name), SqlCatalogTable>(TableNameComparer.Instance);
            var indexes = new List<SqlCatalogIndex>();
            var views = new Dictionary<string, SqlCatalogView>(StringComparer.OrdinalIgnoreCase);
            foreach (var retired in _truncatedTables.OrderBy(pair => pair.Value.Marker))
            {
                if (snapshot.IsVisible(retired.Value.Marker)) { continue; }
                var before = retired.Value.Before;
                var table = before.Tables.Single(table => table.ObjectId == retired.Key);
                // Multiple truncations can overlap one reader: keep the first owner it
                // predates, rather than a later empty replacement of the same table.
                if (!tables.TryAdd((table.Schema, table.Name), table)) { continue; }
                indexes.AddRange(before.GetIndexes(table.ObjectId));
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
                _views.Where(pair => !views.ContainsKey(pair.Key)).Select(pair => pair.Value.Value).Concat(views.Values));
        }
    }

    // Internal engine operations: no new cross-assembly catalog seam. Callers hold table X
    // locks in ascending identity order, then enter this metadata lock. No data apply gate
    // or tree/page latch is held here. Catalog storage brackets are strictly innermost.
    internal SqlCatalogTable[] ReserveTruncate(IReadOnlyList<SqlCatalogTable> expected, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTruncateSet(expected);
            ulong next = _nextObjectId;
            var replacements = expected.Select(table => new SqlCatalogTable(checked(next++), table.Schema, table.Name,
                table.Columns, table.PrimaryKeyColumns, table.Owner, table.OwningSchema, table.Constraints,
                table.DroppedColumnOrdinals)).ToArray();
            using var transaction = _storage.BeginTransaction();
            // Reuse kind 14 for unreachable prepared owners too. A failed build or crash
            // leaves durable reclamation work. The active statement pins these owners until
            // publication/cleanup has finished; successful publication removes these intents.
            var locations = replacements.Select(table => _storage.InsertRow(transaction, table.ObjectId,
                SqlReclamationRecord.Encode(table.ObjectId, table.Schema, table.Name))).ToArray();
            var counterLocation = PersistCounter(transaction, next, _ddlGeneration);
            transaction.Commit();
            _counterLocation = counterLocation;
            _nextObjectId = next;
            for (int index = 0; index < replacements.Length; index++)
            {
                _pendingReclamations.Add(replacements[index].ObjectId, locations[index]);
            }
            return replacements;
        }
    }

    internal ValueTask PublishTruncateAsync(IReadOnlyList<SqlCatalogTable> expected,
        IReadOnlyList<SqlCatalogTable> replacements, IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations, bool restartIdentity, TransactionSequence marker, string actualObject, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            ValidateTruncateSet(expected);
            ulong bindingGeneration = checked(_storageBindingGeneration + 1);
            var ids = expected.Select(table => table.ObjectId).ToHashSet();
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
            var before = new SqlCatalogSnapshot(expected, _indexes.Values.Select(slot => slot.Index).Where(index => ids.Contains(index.TableObjectId)),
                views: _views.Values.Select(slot => slot.Value).Where(view => view.TableDependencies.Any(ids.Contains)));
            var tableRecords = replacements.Select(EncodeTable).ToArray();
            var indexRecords = indexes.Select(EncodeIndex).ToArray();
            using var transaction = _storage.BeginTransaction();
            var tableSlots = replacements.Select((table, index) => new TableSlot(table,
                UpsertRecord(transaction, _tables[(table.Schema, table.Name)].Location, tableRecords[index]))).ToArray();
            var indexSlots = indexes.Select((index, ordinal) => new IndexSlot(index,
                _storage.InsertRow(transaction, indexRecords[ordinal]))).ToArray();
            var viewSlots = views.Select(view => (view.Key, Slot: new DefinitionSlot<SqlCatalogView>(view.View,
                UpsertRecord(transaction, view.Old.Location, view.Record)))).ToArray();
            var oldIndexes = _indexes.Values.Where(slot => ids.Contains(slot.Index.TableObjectId)).ToArray();
            foreach (var slot in oldIndexes) { _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex); }
            var retired = expected.Select(table => (table.ObjectId, Location: _storage.InsertRow(transaction, table.ObjectId,
                SqlReclamationRecord.Encode(table.ObjectId, table.Schema, table.Name)))).ToArray();
            var counters = new List<(ulong Id, IdentityCounter Counter)>();
            foreach (var table in expected)
            {
                DeleteIdentityCounter(transaction, table.ObjectId);
                if (!restartIdentity && _identityCounters.TryGetValue(table.ObjectId, out var previous) && previous.Location is not null)
                {
                    var identity = table.Columns.Single(column => column.Identity is not null).Identity!;
                    long reserved = checked((long)(previous.Next + (Int128)(previous.Remaining - 1) * identity.Increment));
                    ulong id = mapping[table.ObjectId].ObjectId;
                    var record = new DatabaseKeyWriter().AppendInt32(identityCounterRecordKind).AppendInt64((long)id).AppendInt64(reserved).ToArray();
                    counters.Add((id, new IdentityCounter { Next = previous.Next, Remaining = previous.Remaining,
                        Location = _storage.InsertRow(transaction, record) }));
                }
            }
            foreach (var table in replacements)
            {
                var location = _pendingReclamations[table.ObjectId];
                _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
            }
            var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(merged));
            DefinitionSlot<SqlCatalogScript>? receipt = null;
            if (publication is not null)
            {
                ValidateScriptPublication(publication, actualObject);
                receipt = StageScript(transaction, CreateScriptReceipt(publication, _ddlGeneration, actualObject, false, false));
            }
            transaction.Commit();
            if (receipt is not null) { _scripts[receipt.Value.Name] = receipt; }
            foreach (var slot in tableSlots) { _tables[(slot.Table.Schema, slot.Table.Name)] = slot; }
            foreach (var slot in oldIndexes) { _indexes.Remove((slot.Index.TableObjectId, slot.Index.Name)); }
            foreach (var slot in indexSlots) { _indexes[(slot.Index.TableObjectId, slot.Index.Name)] = slot; }
            foreach (var view in viewSlots) { _views[view.Key] = view.Slot; }
            foreach (var table in expected) { _identityCounters.Remove(table.ObjectId); }
            foreach (var counter in counters) { _identityCounters.Add(counter.Id, counter.Counter); }
            foreach (var table in replacements) { _pendingReclamations.Remove(table.ObjectId); }
            foreach (var item in retired) { _pendingReclamations.Add(item.ObjectId, item.Location); }
            foreach (var table in expected) { _truncatedTables.Add(table.ObjectId, (marker, before)); }
            Volatile.Write(ref _truncatedTableCount, _truncatedTables.Count);
            _registrations = merged;
            _registrationsLocation = registrationsLocation;
            _storageBindingGeneration = bindingGeneration;
            return default;
        }
    }

    private void ValidateTruncateSet(IReadOnlyList<SqlCatalogTable> expected)
    {
        foreach (var table in expected)
        {
            if (!_tables.TryGetValue((table.Schema, table.Name), out var slot) || !ReferenceEquals(slot.Table, table))
            {
                throw new SqlCatalogException("CS04064", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.TableIdentityReplaced, table.Schema, table.Name));
            }
        }
        var names = expected.Select(table => (table.Schema, table.Name)).ToHashSet(TableNameComparer.Instance);
        foreach (var table in _tables.Values.Select(slot => slot.Table))
        {
            if (names.Contains((table.Schema, table.Name))) { continue; }
            if (table.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                names.Contains((constraint.ReferencedSchema!, constraint.ReferencedTable!))))
            {
                throw new SqlCatalogException("CS03067", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.TruncateForeignKeyDependent, table.Schema, table.Name));
            }
        }
    }

    internal bool IsTableReclamationPending(ulong objectId)
    {
        lock (_sync) { ThrowIfOffline(); return _pendingReclamations.ContainsKey(objectId); }
    }
}
