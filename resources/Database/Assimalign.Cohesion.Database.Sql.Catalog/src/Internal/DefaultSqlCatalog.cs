using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// Default catalog: metadata records on a dedicated catalog storage file set,
/// encoded with the shared self-describing tuple codec, one record per table plus
/// one for the object-id counter and one for index registrations. Records rewrite
/// in place when they fit and relocate (delete + insert) when they grow.
/// </summary>
internal sealed class DefaultSqlCatalog : ISqlCatalog
{
    private const int tableRecordKind = 1;
    private const int counterRecordKind = 2;
    private const int indexRegistrationsKind = 3;
    private const int recordSpaceFormatKind = 4;
    private const int indexRecordKind = 5;
    private const int schemaStateRecordKind = 6;
    private const int defaultCollationRecordKind = 7;
    private const int schemaStateChunkSize = 3 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly SqlStorage _storage;
    private readonly Dictionary<(string Schema, string Name), TableSlot> _tables = new(TableNameComparer.Instance);
    private readonly Dictionary<(ulong TableObjectId, string Name), IndexSlot> _indexes = new(IndexNameComparer.Instance);
    private readonly object _sync = new();
    private ulong _nextObjectId = 1;
    private int _recordSpaceFormatVersion = 1;
    private Collation _defaultCollation = Collation.Binary;
    private (PageId PageId, int SlotIndex)? _counterLocation;
    private (PageId PageId, int SlotIndex)? _registrationsLocation;
    private (PageId PageId, int SlotIndex)? _formatLocation;
    private (PageId PageId, int SlotIndex)? _defaultCollationLocation;
    private List<(PageId PageId, int SlotIndex)> _schemaStateLocations = [];
    private IReadOnlyList<BTreeIndexRegistration> _registrations = Array.Empty<BTreeIndexRegistration>();
    private SqlCatalogSchemaState? _schemaState;

    private DefaultSqlCatalog(SqlStorage storage)
    {
        _storage = storage;
    }

    internal static DefaultSqlCatalog Open(SqlStorage storage, Collation? defaultCollation = null)
    {
        var catalog = new DefaultSqlCatalog(storage);
        catalog.Load();
        if (defaultCollation is not null)
        {
            catalog.InitializeDefaultCollation(defaultCollation);
        }
        return catalog;
    }

    // Exposed through SqlCatalog.CaptureSnapshot(ISqlCatalog) without adding a capability
    // to the public catalog contract.
    internal ISqlCatalogSnapshot CaptureSnapshot()
    {
        lock (_sync)
        {
            return new SqlCatalogSnapshot(_tables.Values.Select(slot => slot.Table), _indexes.Values.Select(slot => slot.Index), _defaultCollation);
        }
    }

    /// <inheritdoc />
    public Collation DefaultCollation
    {
        get
        {
            lock (_sync)
            {
                return _defaultCollation;
            }
        }
    }

    /// <summary>
    /// Establishes the database default collation at open time. Only a catalog with no
    /// tables can adopt a different default: an existing table's index keys are encoded
    /// through the collation it inherited, so changing it would invalidate them.
    /// </summary>
    /// <param name="collation">The database default collation.</param>
    /// <exception cref="SqlCatalogException">The catalog contains tables and the default would change.</exception>
    private void InitializeDefaultCollation(Collation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        lock (_sync)
        {
            if (collation.Id == _defaultCollation.Id)
            {
                return;
            }
            if (_tables.Count > 0)
            {
                throw new SqlCatalogException("The database default collation cannot change after tables are created; existing index keys depend on it.");
            }

            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(defaultCollationRecordKind).AppendInt8((sbyte)collation.Id);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, _defaultCollationLocation, writer.ToArray());
            transaction.Commit();
            _defaultCollationLocation = location;
            _defaultCollation = collation;
        }
    }

    /// <inheritdoc />
    public SqlCatalogSchemaState? SchemaState
    {
        get
        {
            lock (_sync)
            {
                return _schemaState;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SqlCatalogTable> Tables
    {
        get
        {
            lock (_sync)
            {
                return _tables.Values.Select(slot => slot.Table).ToList();
            }
        }
    }

    /// <inheritdoc />
    public bool TryGetTable(string schema, string name, out SqlCatalogTable table)
    {
        lock (_sync)
        {
            if (_tables.TryGetValue((schema, name), out var slot))
            {
                table = slot.Table;
                return true;
            }
        }

        table = null!;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns = null,
        CancellationToken cancellationToken = default)
        => CreateTableAsync(schema, name, columns, primaryKeyColumns,
            DatabaseObjectOwner.Adhoc, owningSchema: null, cancellationToken);

    internal ValueTask<SqlCatalogTable> CreateTableAsync(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns,
        DatabaseObjectOwner owner,
        string? owningSchema,
        CancellationToken cancellationToken,
        IReadOnlyList<SqlCatalogConstraint>? constraints = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_tables.ContainsKey((schema, name)))
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' already exists.");
            }

            ValidateColumns(schema, name, columns, primaryKeyColumns);

            var table = new SqlCatalogTable(_nextObjectId++, schema, name, columns, primaryKeyColumns, owner, owningSchema, constraints);
            ValidateConstraints(table);

            using (var transaction = _storage.BeginTransaction())
            {
                var location = _storage.InsertRow(transaction, EncodeTable(table));
                PersistCounter(transaction);
                transaction.Commit();
                _tables[(schema, name)] = new TableSlot(table, location);
            }

            return new ValueTask<SqlCatalogTable>(table);
        }
    }

    /// <inheritdoc />
    public ValueTask DropTableAsync(string schema, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_tables.TryGetValue((schema, name), out var slot))
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' does not exist.");
            }

            foreach (var dependent in _tables.Values)
            {
                if (dependent.Table.ObjectId != slot.Table.ObjectId && dependent.Table.Constraints.Any(constraint =>
                    constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    string.Equals(constraint.ReferencedSchema, schema, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(constraint.ReferencedTable, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SqlCatalogException($"Table '{schema}.{name}' is referenced by '{dependent.Table.Schema}.{dependent.Table.Name}'. Drop the foreign key first.");
                }
            }

            // The table's index descriptions and registrations fall with it, in the
            // same self-committing transaction — a dropped table must not leave a
            // description promising an index, nor a registration re-attaching one.
            var droppedIndexes = new List<IndexSlot>();
            foreach (var indexSlot in _indexes.Values)
            {
                if (indexSlot.Index.TableObjectId == slot.Table.ObjectId)
                {
                    droppedIndexes.Add(indexSlot);
                }
            }

            var remainingRegistrations = new List<BTreeIndexRegistration>();
            foreach (var registration in _registrations)
            {
                if (registration.ObjectId != slot.Table.ObjectId)
                {
                    remainingRegistrations.Add(registration);
                }
            }

            using (var transaction = _storage.BeginTransaction())
            {
                _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);

                foreach (var indexSlot in droppedIndexes)
                {
                    _storage.DeleteRow(transaction, indexSlot.Location.PageId, indexSlot.Location.SlotIndex);
                }

                if (droppedIndexes.Count > 0)
                {
                    _registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(remainingRegistrations));
                }

                transaction.Commit();
            }

            _tables.Remove((schema, name));

            foreach (var indexSlot in droppedIndexes)
            {
                _indexes.Remove((indexSlot.Index.TableObjectId, indexSlot.Index.Name));
            }

            if (droppedIndexes.Count > 0)
            {
                _registrations = remainingRegistrations;
            }

            return default;
        }
    }

    /// <inheritdoc />
    public ValueTask<SqlCatalogTable> AddColumnAsync(string schema, string name, SqlCatalogColumn column, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(column);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var slot = GetSlot(schema, name);

            if (slot.Table.FindColumn(column.Name) is not null)
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' already has a column named '{column.Name}'.");
            }

            var columns = slot.Table.Columns.Append(column).ToList();
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns,
                slot.Table.Owner, slot.Table.OwningSchema, slot.Table.Constraints);
            ReplaceTable(slot, updated);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    /// <inheritdoc />
    public ValueTask<SqlCatalogTable> DropColumnAsync(string schema, string name, string columnName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var slot = GetSlot(schema, name);

            if (slot.Table.FindColumn(columnName) is null)
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' has no column named '{columnName}'.");
            }

            if (slot.Table.PrimaryKeyColumns.Any(pk => string.Equals(pk, columnName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlCatalogException($"Column '{columnName}' is part of the primary key of '{schema}.{name}' and cannot be dropped.");
            }

            foreach (var dependent in _tables.Values)
            {
                foreach (var constraint in dependent.Table.Constraints)
                {
                    bool local = dependent.Table.ObjectId == slot.Table.ObjectId &&
                        constraint.Columns.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
                    bool referenced = constraint.Kind == SqlCatalogConstraintKind.Reference &&
                        string.Equals(constraint.ReferencedSchema, schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(constraint.ReferencedTable, name, StringComparison.OrdinalIgnoreCase) &&
                        constraint.ReferencedColumns.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
                    if (local || referenced)
                    {
                        throw new SqlCatalogException($"Column '{columnName}' is referenced by constraint '{constraint.Name}'. Drop the constraint first.");
                    }
                }
            }

            // An indexed column cannot be dropped: index entries key on the column's
            // values (and row rewrites must never invalidate live entry references).
            foreach (var indexSlot in _indexes.Values)
            {
                if (indexSlot.Index.TableObjectId == slot.Table.ObjectId &&
                    indexSlot.Index.ColumnNames.Any(column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new SqlCatalogException(
                        $"Column '{columnName}' is referenced by index '{indexSlot.Index.Name}' on '{schema}.{name}'. Drop the index first.");
                }
            }

            if (slot.Table.Columns.Count == 1)
            {
                throw new SqlCatalogException($"Cannot drop the last column of '{schema}.{name}'.");
            }

            var columns = slot.Table.Columns
                .Where(c => !string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns,
                slot.Table.Owner, slot.Table.OwningSchema, slot.Table.Constraints);
            ReplaceTable(slot, updated);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SqlCatalogIndex> GetIndexes(ulong tableObjectId)
    {
        lock (_sync)
        {
            var result = new List<SqlCatalogIndex>();

            foreach (var slot in _indexes.Values)
            {
                if (slot.Index.TableObjectId == tableObjectId)
                {
                    result.Add(slot.Index);
                }
            }

            return result;
        }
    }

    /// <inheritdoc />
    public bool TryGetIndex(ulong tableObjectId, string name, out SqlCatalogIndex index)
    {
        lock (_sync)
        {
            if (_indexes.TryGetValue((tableObjectId, name), out var slot))
            {
                index = slot.Index;
                return true;
            }
        }

        index = null!;
        return false;
    }

    /// <inheritdoc />
    public ValueTask<SqlCatalogIndex> CreateIndexAsync(SqlCatalogIndex index, IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var table = FindTableByObjectId(index.TableObjectId)
                ?? throw new SqlCatalogException($"No table with object id {index.TableObjectId} exists.");

            if (_indexes.ContainsKey((index.TableObjectId, index.Name)) ||
                table.Constraints.Any(constraint => string.Equals(constraint.Name, index.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlCatalogException($"An index named '{index.Name}' already exists on '{table.Schema}.{table.Name}'.");
            }

            foreach (string column in index.ColumnNames)
            {
                if (table.FindColumn(column) is null)
                {
                    throw new SqlCatalogException($"Index '{index.Name}': table '{table.Schema}.{table.Name}' has no column named '{column}'.");
                }
            }

            using (var transaction = _storage.BeginTransaction())
            {
                var location = _storage.InsertRow(transaction, EncodeIndex(index));
                _registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(registrations));
                transaction.Commit();
                _indexes[(index.TableObjectId, index.Name)] = new IndexSlot(index, location);
            }

            _registrations = registrations.ToList();
            return new ValueTask<SqlCatalogIndex>(index);
        }
    }

    /// <inheritdoc />
    public ValueTask DropIndexAsync(ulong tableObjectId, string name, IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (!_indexes.TryGetValue((tableObjectId, name), out var slot))
            {
                throw new SqlCatalogException($"No index named '{name}' exists on object {tableObjectId}.");
            }

            using (var transaction = _storage.BeginTransaction())
            {
                _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
                _registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(registrations));
                transaction.Commit();
            }

            _indexes.Remove((tableObjectId, name));
            _registrations = registrations.ToList();
            return default;
        }
    }

    /// <inheritdoc />
    public ValueTask SaveIndexRegistrationsAsync(IReadOnlyList<BTreeIndexRegistration> registrations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            byte[] record = EncodeRegistrations(registrations);

            using (var transaction = _storage.BeginTransaction())
            {
                _registrationsLocation = UpsertRecord(transaction, _registrationsLocation, record);
                transaction.Commit();
            }

            _registrations = registrations.ToList();
            return default;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<BTreeIndexRegistration> GetIndexRegistrations()
    {
        lock (_sync)
        {
            return _registrations;
        }
    }

    /// <inheritdoc />
    public int RecordSpaceFormatVersion
    {
        get
        {
            lock (_sync)
            {
                return _recordSpaceFormatVersion;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask SetRecordSpaceFormatVersionAsync(int version, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            var writer = new DatabaseKeyWriter();
            writer.AppendInt32(recordSpaceFormatKind).AppendInt32(version);
            byte[] record = writer.ToArray();

            using (var transaction = _storage.BeginTransaction())
            {
                _formatLocation = UpsertRecord(transaction, _formatLocation, record);
                transaction.Commit();
            }

            _recordSpaceFormatVersion = version;
            return default;
        }
    }

    /// <inheritdoc />
    public ValueTask SaveSchemaStateAsync(SqlCatalogSchemaState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] document = StrictUtf8.GetBytes(state.CanonicalDocument);
        int chunkCount = document.Length == 0
            ? 1
            : ((document.Length - 1) / schemaStateChunkSize) + 1;
        var records = new List<byte[]>(chunkCount);

        for (int index = 0; index < chunkCount; index++)
        {
            int offset = index * schemaStateChunkSize;
            int length = Math.Min(schemaStateChunkSize, document.Length - offset);
            records.Add(EncodeSchemaStateChunk(
                state.ContentHash,
                index,
                chunkCount,
                document.AsSpan(offset, length)));
        }

        lock (_sync)
        {
            var locations = new List<(PageId PageId, int SlotIndex)>(records.Count);

            using (var transaction = _storage.BeginTransaction())
            {
                foreach (var location in _schemaStateLocations)
                {
                    _storage.DeleteRow(transaction, location.PageId, location.SlotIndex);
                }

                foreach (byte[] record in records)
                {
                    locations.Add(_storage.InsertRow(transaction, record));
                }

                transaction.Commit();
            }

            _schemaStateLocations = locations;
            _schemaState = state;
            return default;
        }
    }

    // ── Persistence ────────────────────────────────────────────────────

    private void Load()
    {
        var schemaStateChunks = new List<SchemaStateChunk>();
        using var iterator = _storage.GetUnitIterator();

        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            var reader = new DatabaseKeyReader(unit.Data.Span);
            int kind = reader.ReadInt32();

            switch (kind)
            {
                case tableRecordKind:
                    var table = DecodeTable(ref reader);
                    _tables[(table.Schema, table.Name)] = new TableSlot(table, (unit.PageId, unit.SlotIndex));

                    if (table.ObjectId >= _nextObjectId)
                    {
                        _nextObjectId = table.ObjectId + 1;
                    }

                    break;

                case counterRecordKind:
                    ulong persisted = (ulong)reader.ReadInt64();

                    if (persisted > _nextObjectId)
                    {
                        _nextObjectId = persisted;
                    }

                    _counterLocation = (unit.PageId, unit.SlotIndex);
                    break;

                case indexRegistrationsKind:
                    _registrations = DecodeRegistrations(ref reader);
                    _registrationsLocation = (unit.PageId, unit.SlotIndex);
                    break;

                case recordSpaceFormatKind:
                    _recordSpaceFormatVersion = reader.ReadInt32();
                    _formatLocation = (unit.PageId, unit.SlotIndex);
                    break;

                case indexRecordKind:
                    var index = DecodeIndex(ref reader);
                    _indexes[(index.TableObjectId, index.Name)] = new IndexSlot(index, (unit.PageId, unit.SlotIndex));
                    break;

                case schemaStateRecordKind:
                    var chunk = DecodeSchemaStateChunk(ref reader);
                    schemaStateChunks.Add(new SchemaStateChunk(
                        chunk.ContentHash,
                        chunk.Index,
                        chunk.Count,
                        chunk.Document,
                        (unit.PageId, unit.SlotIndex)));
                    break;

                case defaultCollationRecordKind:
                    _defaultCollation = Collation.FromId((byte)reader.ReadInt8());
                    if (!reader.IsAtEnd)
                    {
                        throw new SqlCatalogException("The persisted default collation contains trailing values.");
                    }
                    _defaultCollationLocation = (unit.PageId, unit.SlotIndex);
                    break;

                default:
                    throw new SqlCatalogException($"Malformed catalog record of kind {kind}.");
            }
        }

        if (schemaStateChunks.Count > 0)
        {
            _schemaState = AssembleSchemaState(schemaStateChunks);
            _schemaStateLocations = schemaStateChunks.Select(chunk => chunk.Location).ToList();
        }
    }

    private TableSlot GetSlot(string schema, string name)
    {
        if (!_tables.TryGetValue((schema, name), out var slot))
        {
            throw new SqlCatalogException($"Table '{schema}.{name}' does not exist.");
        }

        return slot;
    }

    private SqlCatalogTable? FindTableByObjectId(ulong objectId)
    {
        foreach (var slot in _tables.Values)
        {
            if (slot.Table.ObjectId == objectId)
            {
                return slot.Table;
            }
        }

        return null;
    }

    private void ReplaceTable(TableSlot slot, SqlCatalogTable updated)
    {
        byte[] record = EncodeTable(updated);

        using (var transaction = _storage.BeginTransaction())
        {
            var location = UpsertRecord(transaction, slot.Location, record);
            transaction.Commit();
            _tables[(updated.Schema, updated.Name)] = new TableSlot(updated, location);
        }
    }

    /// <summary>
    /// Rewrites a record in place when it fits, relocating it otherwise.
    /// </summary>
    private (PageId PageId, int SlotIndex) UpsertRecord(
        IStorageTransaction transaction,
        (PageId PageId, int SlotIndex)? location,
        byte[] record)
    {
        if (location is null)
        {
            return _storage.InsertRow(transaction, record);
        }

        try
        {
            _storage.UpdateRow(transaction, location.Value.PageId, location.Value.SlotIndex, record);
            return location.Value;
        }
        catch (SlottedPageException)
        {
            _storage.DeleteRow(transaction, location.Value.PageId, location.Value.SlotIndex);
            return _storage.InsertRow(transaction, record);
        }
    }

    private void PersistCounter(IStorageTransaction transaction)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(counterRecordKind).AppendInt64((long)_nextObjectId);
        _counterLocation = UpsertRecord(transaction, _counterLocation, writer.ToArray());
    }

    private static void ValidateColumns(
        string schema,
        string name,
        IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            if (!seen.Add(column.Name))
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' declares column '{column.Name}' more than once.");
            }
        }

        if (primaryKeyColumns is not null)
        {
            foreach (var pk in primaryKeyColumns)
            {
                if (!seen.Contains(pk))
                {
                    throw new SqlCatalogException($"Primary-key column '{pk}' is not a column of '{schema}.{name}'.");
                }
            }
        }
    }

    // ── Record codec (shared self-describing tuple encoding) ──────────

    private static byte[] EncodeTable(SqlCatalogTable table)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(tableRecordKind)
              .AppendInt64((long)table.ObjectId)
              .AppendString(table.Schema, Collation.Binary)
              .AppendString(table.Name, Collation.Binary)
              .AppendInt32(table.Columns.Count);

        foreach (var column in table.Columns)
        {
            writer.AppendString(column.Name, Collation.Binary)
                  .AppendInt8((sbyte)column.Type.Type)
                  .AppendInt32(column.Type.MaxLength ?? -1)
                  .AppendInt32(column.Type.Precision ?? -1)
                  .AppendInt32(column.Type.Scale ?? -1)
                  .AppendBoolean(column.IsNullable);

            if (column.DefaultLiteral is null)
            {
                writer.AppendNull();
            }
            else
            {
                writer.AppendString(column.DefaultLiteral, Collation.Binary);
            }
        }

        writer.AppendInt32(table.PrimaryKeyColumns.Count);
        foreach (var pk in table.PrimaryKeyColumns)
        {
            writer.AppendString(pk, Collation.Binary);
        }

        AppendOwnership(ref writer, table.Owner, table.OwningSchema);
        // Versioned trailing extension; old records end after keys or ownership.
        writer.AppendInt32(2).AppendInt32(table.Constraints.Count);
        foreach (SqlCatalogConstraint constraint in table.Constraints)
        {
            writer.AppendString(constraint.Name, Collation.Binary)
                  .AppendInt8((sbyte)constraint.Kind)
                  .AppendInt32(constraint.Columns.Count);
            foreach (string column in constraint.Columns)
            {
                writer.AppendString(column, Collation.Binary);
            }
            AppendOptionalString(ref writer, constraint.ReferencedSchema);
            AppendOptionalString(ref writer, constraint.ReferencedTable);
            writer.AppendInt32(constraint.ReferencedColumns.Count);
            foreach (string column in constraint.ReferencedColumns)
            {
                writer.AppendString(column, Collation.Binary);
            }
            AppendOptionalString(ref writer, constraint.CheckExpression);
            writer.AppendInt8((sbyte)constraint.OnDelete);
        }
        foreach (var column in table.Columns)
        {
            writer.AppendInt32(column.Collation?.Id ?? -1);
        }
        return writer.ToArray();
    }

    private static SqlCatalogTable DecodeTable(ref DatabaseKeyReader reader)
    {
        ulong objectId = (ulong)reader.ReadInt64();
        string schema = reader.ReadString(out _);
        string name = reader.ReadString(out _);
        int columnCount = reader.ReadInt32();

        var columns = new List<SqlCatalogColumn>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            string columnName = reader.ReadString(out _);
            var type = (DatabaseType)reader.ReadInt8();
            int maxLength = reader.ReadInt32();
            int precision = reader.ReadInt32();
            int scale = reader.ReadInt32();
            bool nullable = reader.ReadBoolean();

            string? defaultLiteral = null;
            if (reader.PeekType() == DatabaseType.Null)
            {
                reader.ReadNull();
            }
            else
            {
                defaultLiteral = reader.ReadString(out _);
            }

            columns.Add(new SqlCatalogColumn(
                columnName,
                new DatabaseTypeInfo(
                    type,
                    maxLength < 0 ? null : maxLength,
                    precision < 0 ? null : precision,
                    scale < 0 ? null : scale),
                nullable,
                defaultLiteral));
        }

        int primaryKeyCount = reader.ReadInt32();
        var primaryKey = new List<string>(primaryKeyCount);
        for (int i = 0; i < primaryKeyCount; i++)
        {
            primaryKey.Add(reader.ReadString(out _));
        }

        var (owner, owningSchema) = ReadOwnership(ref reader, allowTrailing: true);
        var constraints = new List<SqlCatalogConstraint>();
        if (!reader.IsAtEnd)
        {
            int version = reader.ReadInt32();
            if (version is not (1 or 2))
            {
                throw new SqlCatalogException($"Unsupported table-constraint metadata version {version}.");
            }
            int count = reader.ReadInt32();
            if (count < 0)
            {
                throw new SqlCatalogException("The persisted constraint count is invalid.");
            }
            for (int index = 0; index < count; index++)
            {
                string constraintName = reader.ReadString(out _);
                var kind = (SqlCatalogConstraintKind)reader.ReadInt8();
                var localColumns = ReadColumnNames(ref reader);
                string? referencedSchema = ReadOptionalString(ref reader);
                string? referencedTable = ReadOptionalString(ref reader);
                var referencedColumns = ReadColumnNames(ref reader);
                string? expression = ReadOptionalString(ref reader);
                var onDelete = (SqlCatalogReferentialAction)reader.ReadInt8();
                try
                {
                    constraints.Add(new SqlCatalogConstraint(constraintName, kind, localColumns,
                        referencedSchema, referencedTable, referencedColumns, expression, onDelete));
                }
                catch (ArgumentException exception)
                {
                    throw new SqlCatalogException($"The persisted constraint '{constraintName}' is invalid: {exception.Message}");
                }
            }
            if (version >= 2)
            {
                for (int index = 0; index < columns.Count; index++)
                {
                    int id = reader.ReadInt32();
                    if (id < -1 || id > byte.MaxValue)
                    {
                        throw new SqlCatalogException($"The persisted column collation identifier {id} is invalid.");
                    }
                    var column = columns[index];
                    columns[index] = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable,
                        column.DefaultLiteral, id < 0 ? null : Collation.FromId((byte)id));
                }
            }
        }
        if (!reader.IsAtEnd)
        {
            throw new SqlCatalogException("The persisted table constraint metadata contains trailing values.");
        }
        var table = new SqlCatalogTable(objectId, schema, name, columns, primaryKey, owner, owningSchema, constraints);
        ValidateConstraints(table);
        return table;
    }

    private static byte[] EncodeIndex(SqlCatalogIndex index)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(indexRecordKind)
              .AppendInt64((long)index.TableObjectId)
              .AppendString(index.Name, Collation.Binary)
              .AppendBoolean(index.IsUnique)
              .AppendInt32(index.ColumnNames.Count);

        foreach (string column in index.ColumnNames)
        {
            writer.AppendString(column, Collation.Binary);
        }

        AppendOwnership(ref writer, index.Owner, index.OwningSchema);
        writer.AppendInt32(1).AppendBoolean(index.IsPrimaryKey);
        return writer.ToArray();
    }

    private static SqlCatalogIndex DecodeIndex(ref DatabaseKeyReader reader)
    {
        ulong tableObjectId = (ulong)reader.ReadInt64();
        string name = reader.ReadString(out _);
        bool isUnique = reader.ReadBoolean();
        int columnCount = reader.ReadInt32();

        var columns = new List<string>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            columns.Add(reader.ReadString(out _));
        }

        var (owner, owningSchema) = ReadOwnership(ref reader, allowTrailing: true);
        bool isPrimaryKey = false;
        if (!reader.IsAtEnd)
        {
            int version = reader.ReadInt32();
            if (version != 1)
            {
                throw new SqlCatalogException($"Unsupported index metadata version {version}.");
            }
            isPrimaryKey = reader.ReadBoolean();
        }
        if (!reader.IsAtEnd || (isPrimaryKey && !isUnique))
        {
            throw new SqlCatalogException("The persisted primary-key index metadata is invalid.");
        }
        return new SqlCatalogIndex(tableObjectId, name, columns, isUnique, owner, owningSchema, isPrimaryKey);
    }

    private static void ValidateConstraints(SqlCatalogTable table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SqlCatalogConstraint constraint in table.Constraints)
        {
            if (!names.Add(constraint.Name))
            {
                throw new SqlCatalogException($"Constraint '{constraint.Name}' is declared more than once on '{table.Schema}.{table.Name}'.");
            }
            foreach (string column in constraint.Columns)
            {
                if (table.FindColumn(column) is null)
                {
                    throw new SqlCatalogException($"Constraint '{constraint.Name}' refers to unknown column '{column}'.");
                }
            }
        }
    }

    private static IReadOnlyList<string> ReadColumnNames(ref DatabaseKeyReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
        {
            throw new SqlCatalogException("The persisted constraint column count is invalid.");
        }
        var columns = new List<string>();
        for (int index = 0; index < count; index++)
        {
            columns.Add(reader.ReadString(out _));
        }
        return columns;
    }

    private static void AppendOptionalString(ref DatabaseKeyWriter writer, string? value)
    {
        if (value is null)
        {
            writer.AppendNull();
        }
        else
        {
            writer.AppendString(value, Collation.Binary);
        }
    }

    private static string? ReadOptionalString(ref DatabaseKeyReader reader)
    {
        if (reader.PeekType() == DatabaseType.Null)
        {
            reader.ReadNull();
            return null;
        }
        return reader.ReadString(out _);
    }

    // Exposed through SqlCatalog.ReserveTableAsync(ISqlCatalog, ...) without adding a
    // capability to the public catalog contract.
    internal ValueTask<SqlCatalogTable> ReserveTableAsync(
        string schema, string name, IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<string>? primaryKeyColumns, IReadOnlyList<SqlCatalogConstraint> constraints,
        DatabaseObjectOwner owner, string? owningSchema, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(constraints);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_tables.ContainsKey((schema, name)))
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' already exists.");
            }
            ValidateColumns(schema, name, columns, primaryKeyColumns);
            var table = new SqlCatalogTable(_nextObjectId++, schema, name, columns, primaryKeyColumns, owner, owningSchema, constraints);
            ValidateConstraints(table);
            // Persist the identity before physical trees are created. A crash can
            // leave unused trees, but can never reuse their identity for a table.
            using var transaction = _storage.BeginTransaction();
            PersistCounter(transaction);
            transaction.Commit();
            return new ValueTask<SqlCatalogTable>(table);
        }
    }

    // Exposed through SqlCatalog.PublishTableAsync(ISqlCatalog, ...) without adding a
    // capability to the public catalog contract.
    internal ValueTask PublishTableAsync(
        SqlCatalogTable table, IReadOnlyList<SqlCatalogIndex> indexes,
        IReadOnlyList<BTreeIndexRegistration> registrations, bool replaceExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(registrations);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!replaceExisting && (table.ObjectId == 0 || table.ObjectId >= _nextObjectId))
            {
                throw new SqlCatalogException($"Table '{table.Schema}.{table.Name}' has no reserved catalog identity.");
            }
            _tables.TryGetValue((table.Schema, table.Name), out TableSlot? existing);
            if (replaceExisting ? existing is null || existing.Table.ObjectId != table.ObjectId :
                existing is not null || FindTableByObjectId(table.ObjectId) is not null)
            {
                throw new SqlCatalogException($"Table '{table.Schema}.{table.Name}' has an unexpected catalog identity.");
            }
            ValidateColumns(table.Schema, table.Name, table.Columns, table.PrimaryKeyColumns);
            ValidateConstraints(table);
            var names = new HashSet<string>(table.Constraints.Select(constraint => constraint.Name), StringComparer.OrdinalIgnoreCase);
            foreach (SqlCatalogIndex index in GetIndexes(table.ObjectId))
            {
                if (!names.Add(index.Name) || index.ColumnNames.Any(column => table.FindColumn(column) is null))
                {
                    throw new SqlCatalogException($"Index '{index.Name}' conflicts with the replacement table definition.");
                }
            }
            foreach (SqlCatalogIndex index in indexes)
            {
                if (index.TableObjectId != table.ObjectId || !names.Add(index.Name) ||
                    index.ColumnNames.Any(column => table.FindColumn(column) is null) ||
                    !registrations.Any(registration => registration.ObjectId == table.ObjectId &&
                        string.Equals(registration.Definition.Name, index.Name, StringComparison.Ordinal) &&
                        registration.Definition.IsUnique == index.IsUnique))
                {
                    throw new SqlCatalogException($"Index '{index.Name}' does not describe a registered index on '{table.Schema}.{table.Name}'.");
                }
            }
            using var transaction = _storage.BeginTransaction();
            var tableLocation = UpsertRecord(transaction, existing?.Location, EncodeTable(table));
            var slots = indexes.Select(index => new IndexSlot(index, _storage.InsertRow(transaction, EncodeIndex(index)))).ToArray();
            var registrationsLocation = UpsertRecord(transaction, _registrationsLocation, EncodeRegistrations(registrations));
            transaction.Commit();
            _tables[(table.Schema, table.Name)] = new TableSlot(table, tableLocation);
            foreach (IndexSlot slot in slots)
            {
                _indexes[(table.ObjectId, slot.Index.Name)] = slot;
            }
            _registrationsLocation = registrationsLocation;
            _registrations = registrations.ToArray();
            return default;
        }
    }

    internal ValueTask<SqlCatalogTable> AddConstraintAsync(
        string schema, string name, SqlCatalogConstraint constraint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(constraint);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var slot = GetSlot(schema, name);
            if (_indexes.ContainsKey((slot.Table.ObjectId, constraint.Name)))
            {
                throw new SqlCatalogException($"Constraint or index '{constraint.Name}' already exists on '{schema}.{name}'.");
            }
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, slot.Table.Columns,
                slot.Table.PrimaryKeyColumns, slot.Table.Owner, slot.Table.OwningSchema,
                slot.Table.Constraints.Append(constraint).ToArray());
            ValidateConstraints(updated);
            ReplaceTable(slot, updated);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    // Exposed through SqlCatalog.DropConstraintAsync(ISqlCatalog, ...) without adding a
    // capability to the public catalog contract.
    internal ValueTask<SqlCatalogTable> DropConstraintAsync(
        string schema, string name, string constraintName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(constraintName);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var slot = GetSlot(schema, name);
            var constraints = slot.Table.Constraints.Where(constraint =>
                !string.Equals(constraint.Name, constraintName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (constraints.Length == slot.Table.Constraints.Count)
            {
                throw new SqlCatalogException($"Constraint '{constraintName}' does not exist on '{schema}.{name}'.");
            }
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, slot.Table.Columns,
                slot.Table.PrimaryKeyColumns, slot.Table.Owner, slot.Table.OwningSchema, constraints);
            ReplaceTable(slot, updated);
            return new ValueTask<SqlCatalogTable>(updated);
        }
    }

    private static void AppendOwnership(ref DatabaseKeyWriter writer, DatabaseObjectOwner owner, string? owningSchema)
    {
        writer.AppendInt8((sbyte)owner);
        if (owningSchema is null)
        {
            writer.AppendNull();
        }
        else
        {
            writer.AppendString(owningSchema, Collation.Binary);
        }
    }

    private static (DatabaseObjectOwner Owner, string? OwningSchema) ReadOwnership(ref DatabaseKeyReader reader, bool allowTrailing = false)
    {
        // Existing records end after the original payload. They predate ownership
        // and retain ad-hoc mutability instead of acquiring a guessed schema owner.
        if (reader.IsAtEnd)
        {
            return (DatabaseObjectOwner.Adhoc, null);
        }

        var owner = (DatabaseObjectOwner)reader.ReadInt8();
        string? owningSchema = null;
        if (reader.PeekType() == DatabaseType.Null)
        {
            reader.ReadNull();
        }
        else
        {
            owningSchema = reader.ReadString(out _);
        }

        if ((!allowTrailing && !reader.IsAtEnd) ||
            owner is not DatabaseObjectOwner.Adhoc and not DatabaseObjectOwner.Schema ||
            (owner == DatabaseObjectOwner.Schema ? string.IsNullOrWhiteSpace(owningSchema) : owningSchema is not null))
        {
            throw new SqlCatalogException("The persisted object ownership metadata is invalid.");
        }

        return (owner, owningSchema);
    }

    private static byte[] EncodeRegistrations(IReadOnlyList<BTreeIndexRegistration> registrations)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(indexRegistrationsKind).AppendInt32(registrations.Count);

        foreach (var registration in registrations)
        {
            writer.AppendInt64((long)registration.ObjectId)
                  .AppendString(registration.Definition.Name, Collation.Binary)
                  .AppendInt8((sbyte)registration.Definition.Kind)
                  .AppendBoolean(registration.Definition.IsUnique)
                  .AppendInt64(registration.RootPageId);
        }

        return writer.ToArray();
    }

    private static IReadOnlyList<BTreeIndexRegistration> DecodeRegistrations(ref DatabaseKeyReader reader)
    {
        int count = reader.ReadInt32();
        var registrations = new List<BTreeIndexRegistration>(count);

        for (int i = 0; i < count; i++)
        {
            ulong objectId = (ulong)reader.ReadInt64();
            string indexName = reader.ReadString(out _);
            var kind = (IndexKind)reader.ReadInt8();
            bool unique = reader.ReadBoolean();
            long rootPageId = reader.ReadInt64();

            registrations.Add(new BTreeIndexRegistration(
                objectId, new IndexDefinition(indexName, kind, unique), rootPageId));
        }

        return registrations;
    }

    private static byte[] EncodeSchemaStateChunk(
        string contentHash,
        int index,
        int count,
        ReadOnlySpan<byte> document)
    {
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(schemaStateRecordKind)
              .AppendString(contentHash, Collation.Binary)
              .AppendInt32(index)
              .AppendInt32(count)
              .AppendBinary(document);
        return writer.ToArray();
    }

    private static SchemaStateChunk DecodeSchemaStateChunk(ref DatabaseKeyReader reader)
        => new(
            reader.ReadString(out _),
            reader.ReadInt32(),
            reader.ReadInt32(),
            reader.ReadBinary(),
            default);

    private static SqlCatalogSchemaState AssembleSchemaState(IReadOnlyList<SchemaStateChunk> chunks)
    {
        SchemaStateChunk first = chunks[0];
        if (first.Count <= 0 || first.Count != chunks.Count)
        {
            throw new SqlCatalogException("Malformed applied-schema state: its chunk count is inconsistent.");
        }

        var ordered = new byte[first.Count][];
        int documentLength = 0;

        foreach (SchemaStateChunk chunk in chunks)
        {
            if (chunk.Count != first.Count ||
                !string.Equals(chunk.ContentHash, first.ContentHash, StringComparison.Ordinal) ||
                chunk.Index < 0 ||
                chunk.Index >= ordered.Length ||
                ordered[chunk.Index] is not null)
            {
                throw new SqlCatalogException("Malformed applied-schema state: its chunks do not describe one complete document.");
            }

            ordered[chunk.Index] = chunk.Document;
            documentLength = checked(documentLength + chunk.Document.Length);
        }

        var document = new byte[documentLength];
        int offset = 0;
        foreach (byte[] chunk in ordered)
        {
            if (chunk is null)
            {
                throw new SqlCatalogException("Malformed applied-schema state: a document chunk is missing.");
            }

            chunk.CopyTo(document, offset);
            offset += chunk.Length;
        }

        try
        {
            return new SqlCatalogSchemaState(first.ContentHash, StrictUtf8.GetString(document));
        }
        catch (DecoderFallbackException)
        {
            throw new SqlCatalogException("Malformed applied-schema state: its canonical document is not valid UTF-8.");
        }
    }

    private sealed record TableSlot(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location);

    private sealed record IndexSlot(SqlCatalogIndex Index, (PageId PageId, int SlotIndex) Location);

    private sealed record SchemaStateChunk(
        string ContentHash,
        int Index,
        int Count,
        byte[] Document,
        (PageId PageId, int SlotIndex) Location);

    private sealed class IndexNameComparer : IEqualityComparer<(ulong TableObjectId, string Name)>
    {
        internal static IndexNameComparer Instance { get; } = new();

        public bool Equals((ulong TableObjectId, string Name) x, (ulong TableObjectId, string Name) y)
            => x.TableObjectId == y.TableObjectId
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ulong TableObjectId, string Name) value)
            => HashCode.Combine(value.TableObjectId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    private sealed class TableNameComparer : IEqualityComparer<(string Schema, string Name)>
    {
        internal static TableNameComparer Instance { get; } = new();

        public bool Equals((string Schema, string Name) x, (string Schema, string Name) y)
            => string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Name) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }
}
