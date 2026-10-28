using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private const int namespaceRecordKind = SqlCatalogCreation.NamespaceRecordKind;
    private readonly Dictionary<string, DefinitionSlot<SqlCatalogNamespace>> _namespaces = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SqlCatalogNamespace>? _namespaceCapture;

    /// <summary>Gets an immutable capture of the persisted SQL namespaces.</summary>
    /// <exception cref="StorageOfflineException">The catalog is offline.</exception>
    public IReadOnlyList<SqlCatalogNamespace> Namespaces
    {
        get { lock (_sync) { ThrowIfOffline(); return _namespaceCapture ??= Array.AsReadOnly(_namespaces.Values.Select(slot => slot.Value).ToArray()); } }
    }

    /// <summary>Creates a SQL namespace in one durable catalog transaction.</summary>
    /// <param name="schema">The namespace metadata.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">The namespace is null.</exception>
    /// <exception cref="SqlCatalogException">The name exists or its record is too large.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage is offline or cannot persist the change.</exception>
    public ValueTask CreateSchemaAsync(SqlCatalogNamespace schema, CancellationToken cancellationToken = default)
        => CreateSchemaCoreAsync(schema, null, cancellationToken);

    /// <summary>Creates a SQL namespace and its declared-script receipt atomically.</summary>
    /// <param name="schema">The namespace metadata.</param>
    /// <param name="publication">The expected target, preceding receipt and catalog generation.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="SqlCatalogException">The name exists, the record is too large or the publication is stale.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage is offline or cannot persist the change.</exception>
    public ValueTask CreateSchemaAsync(SqlCatalogNamespace schema, SqlCatalogScriptPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return CreateSchemaCoreAsync(schema, publication, cancellationToken);
    }

    private ValueTask CreateSchemaCoreAsync(SqlCatalogNamespace schema, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return CreateDefinitionAsync(_namespaces, schema.Name, schema, namespaceRecordKind,
            SqlCatalogJsonContext.Default.SqlCatalogNamespace, cancellationToken, publication, SqlObjectIdentity.Schema(schema.Name));
    }

    /// <summary>Drops a SQL namespace and, with CASCADE, its tables, indexes, views, external dependent views and incoming foreign keys in one transaction.</summary>
    /// <param name="name">The SQL namespace name.</param>
    /// <param name="cascade">Whether dependent tables, views and incoming constraints are removed; false means RESTRICT.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <remarks>The SQL executor owns DDL interlocks, object locks and ownership checks. This catalog operation serializes publication only.</remarks>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="SqlCatalogException">The namespace is absent or RESTRICT finds contained objects.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage is offline or cannot persist the change.</exception>
    public ValueTask DropSchemaAsync(string name, bool cascade = false, CancellationToken cancellationToken = default)
        => DropSchemaCoreAsync(name, cascade, null, cancellationToken);

    /// <summary>Drops a SQL namespace and records its declared-script statement atomically.</summary>
    /// <param name="name">The SQL namespace name.</param>
    /// <param name="cascade">Whether dependent tables, views and incoming constraints are removed.</param>
    /// <param name="publication">The expected target, preceding receipt and catalog generation. An absent target records an absence receipt.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A completed operation after the durable commit.</returns>
    /// <remarks>The SQL executor owns DDL interlocks, object locks and ownership checks.</remarks>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    /// <exception cref="ArgumentNullException">The publication is null.</exception>
    /// <exception cref="SqlCatalogException">RESTRICT finds contained objects or the publication is stale.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    /// <exception cref="StorageException">Storage is offline or cannot persist the change.</exception>
    public ValueTask DropSchemaAsync(string name, bool cascade, SqlCatalogScriptPublication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return DropSchemaCoreAsync(name, cascade, publication, cancellationToken);
    }

    // Commit-time invariant guard. SQL admission uses the snapshot enumeration in
    // SqlPlanExecutor.Schemas.ObjectsInNamespace for RESTRICT, locks and ownership (#1124).
    private TableSlot[] ObjectsInNamespace(string name)
        => _tables.Values.Where(slot => slot.Table.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

    private ValueTask DropSchemaCoreAsync(string name, bool cascade, SqlCatalogScriptPublication? publication, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            if (!_namespaces.TryGetValue(name, out var schema))
            {
                if (publication is not null) { CommitScriptAbsence(publication, SqlObjectIdentity.Schema(name)); return default; }
                throw new SqlCatalogException("CS01028", string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineNamespaceMissing, name));
            }
            var sequences = _sequences.Values.Where(slot => slot.Value.Schema.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var objects = ObjectsInNamespace(name);
            var ids = objects.Select(slot => slot.Table.ObjectId).ToHashSet();
            var droppedViews = _views.Where(pair => pair.Value.Value.Schema.Equals(name, StringComparison.OrdinalIgnoreCase) || pair.Value.Value.TableDependencies.Any(ids.Contains)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            bool added;
            do
            {
                added = false;
                foreach (var pair in _views)
                { if (pair.Value.Value.Dependencies.Any(droppedViews.ContainsKey)) { added |= droppedViews.TryAdd(pair.Key, pair.Value); } }
            } while (added);
            if (!cascade && droppedViews.Count != 0) { RefuseViewDependency(droppedViews.Values.First().Value); }
            if (!cascade && (objects.Length != 0 || sequences.Length != 0))
            {
                throw new SqlCatalogException("CS03030", string.Format(CultureInfo.CurrentCulture, ErrorMessages.EngineNamespaceDependentObjects, schema.Value.Name));
            }
            var identities = objects.Select(slot => slot.Table.ObjectId).ToHashSet();
            bool ReferencesDropped(SqlCatalogConstraint constraint) => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                constraint.ReferencedSchema!.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                objects.Any(slot => slot.Table.Name.Equals(constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase));
            // A foreign key depends on its parent; its child table does not (PostgreSQL
            // src/backend/catalog/pg_constraint.c:304-324). Keep external tables and remove only the FK.
            var replacements = new List<(TableSlot Old, SqlCatalogTable Table, byte[] Record)>();
            foreach (var slot in _tables.Values.Where(slot => !identities.Contains(slot.Table.ObjectId) && slot.Table.Constraints.Any(ReferencesDropped)))
            {
                var table = slot.Table;
                var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, table.Columns, table.PrimaryKeyColumns,
                    table.Owner, table.OwningSchema, table.Constraints.Where(constraint => !ReferencesDropped(constraint)).ToArray(), table.DroppedColumnOrdinals);
                replacements.Add((slot, replacement, EncodeTable(replacement)));
            }
            var indexes = _indexes.Values.Where(slot => identities.Contains(slot.Index.TableObjectId)).ToArray();
            var registrations = _registrations.Where(registration => !identities.Contains(registration.ObjectId)).ToArray();
            using var transaction = _storage.BeginTransaction();
            var pending = new Dictionary<ulong, (PageId PageId, int SlotIndex)>();
            foreach (var slot in objects)
            {
                var table = slot.Table;
                pending.Add(table.ObjectId, _storage.InsertRow(transaction, table.ObjectId, SqlReclamationRecord.Encode(table.ObjectId, table.Schema, table.Name)));
                _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
                DeleteIdentityCounter(transaction, table.ObjectId);
            }
            foreach (var slot in indexes) { _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex); }
            var tableSlots = replacements.Select(item => new TableSlot(item.Table, UpsertRecord(transaction, item.Old.Location, item.Record))).ToArray();
            var registrationsLocation = _registrationsLocation;
            if (indexes.Length != 0) { registrationsLocation = UpsertRecord(transaction, registrationsLocation, EncodeRegistrations(registrations)); }
            foreach (var view in droppedViews.Values) { _storage.DeleteRow(transaction, view.Location.PageId, view.Location.SlotIndex); }
            foreach (var sequence in sequences) { DeleteSequence(transaction, sequence); }
            _storage.DeleteRow(transaction, schema.Location.PageId, schema.Location.SlotIndex);
            CommitCatalogChange(transaction, publication: publication, actualObject: SqlObjectIdentity.Schema(schema.Value.Name));
            foreach (var key in droppedViews.Keys) { _views.Remove(key); }
            foreach (var sequence in sequences)
            {
                _sequences.Remove((sequence.Value.Schema, sequence.Value.Name));
                _sequencesById.Remove(sequence.Value.ObjectId);
                _sequenceCounters.Remove(sequence.Value.ObjectId);
            }
            _namespaces.Remove(name);
            _namespaceCapture = null;
            foreach (var slot in objects)
            {
                _tables.Remove((slot.Table.Schema, slot.Table.Name));
                _identityCounters.Remove(slot.Table.ObjectId);
                var location = pending[slot.Table.ObjectId];
                _pendingReclamations.Add(slot.Table.ObjectId, new SqlReclamationIntent(location.PageId, location.SlotIndex));
            }
            foreach (var slot in indexes) { _indexes.Remove((slot.Index.TableObjectId, slot.Index.Name)); }
            foreach (var slot in tableSlots) { _tables[(slot.Table.Schema, slot.Table.Name)] = slot; }
            _registrationsLocation = registrationsLocation;
            if (indexes.Length != 0) { _registrations = registrations; }
            return default;
        }
    }

    private void LoadNamespace(string json, (PageId PageId, int SlotIndex) location)
    {
        try
        {
            var value = JsonSerializer.Deserialize(json, SqlCatalogJsonContext.Default.SqlCatalogNamespace);
            if (value is null || !_namespaces.TryAdd(value.Name, new DefinitionSlot<SqlCatalogNamespace>(value, location)))
            {
                throw new SqlCatalogException("CS06059", ErrorMessages.NamespaceRecordMalformed);
            }
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw new SqlCatalogException("CS06059", ErrorMessages.NamespaceRecordMalformed);
        }
    }
}
