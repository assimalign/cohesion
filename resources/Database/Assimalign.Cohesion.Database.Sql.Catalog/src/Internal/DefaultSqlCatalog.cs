using System;
using System.Collections.Generic;
using System.Linq;
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

    private readonly SqlStorage _storage;
    private readonly Dictionary<(string Schema, string Name), TableSlot> _tables = new(TableNameComparer.Instance);
    private readonly Dictionary<(ulong TableObjectId, string Name), IndexSlot> _indexes = new(IndexNameComparer.Instance);
    private readonly object _sync = new();
    private ulong _nextObjectId = 1;
    private int _recordSpaceFormatVersion = 1;
    private (PageId PageId, int SlotIndex)? _counterLocation;
    private (PageId PageId, int SlotIndex)? _registrationsLocation;
    private (PageId PageId, int SlotIndex)? _formatLocation;
    private IReadOnlyList<BTreeIndexRegistration> _registrations = Array.Empty<BTreeIndexRegistration>();

    private DefaultSqlCatalog(SqlStorage storage)
    {
        _storage = storage;
    }

    internal static DefaultSqlCatalog Open(SqlStorage storage)
    {
        var catalog = new DefaultSqlCatalog(storage);
        catalog.Load();
        return catalog;
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
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_tables.ContainsKey((schema, name)))
            {
                throw new SqlCatalogException($"Table '{schema}.{name}' already exists.");
            }

            ValidateColumns(schema, name, columns, primaryKeyColumns);

            var table = new SqlCatalogTable(_nextObjectId++, schema, name, columns, primaryKeyColumns);

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
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns);
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
            var updated = new SqlCatalogTable(slot.Table.ObjectId, schema, name, columns, slot.Table.PrimaryKeyColumns);
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

            if (_indexes.ContainsKey((index.TableObjectId, index.Name)))
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

    // ── Persistence ────────────────────────────────────────────────────

    private void Load()
    {
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

                default:
                    throw new SqlCatalogException($"Malformed catalog record of kind {kind}.");
            }
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

        return new SqlCatalogTable(objectId, schema, name, columns, primaryKey);
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

        return new SqlCatalogIndex(tableObjectId, name, columns, isUnique);
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

    private sealed record TableSlot(SqlCatalogTable Table, (PageId PageId, int SlotIndex) Location);

    private sealed record IndexSlot(SqlCatalogIndex Index, (PageId PageId, int SlotIndex) Location);

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
