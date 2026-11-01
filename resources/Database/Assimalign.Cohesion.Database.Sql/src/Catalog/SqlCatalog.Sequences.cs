using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private const int sequenceRecordKind = 18;
    private const int sequenceCounterRecordKind = 19;
    private readonly Dictionary<(string Schema, string Name), DefinitionSlot<SqlCatalogSequence>> _sequences = new(TableNameComparer.Instance);
    private readonly Dictionary<ulong, SqlSequenceCounter> _sequenceCounters = new();
    private readonly Dictionary<ulong, SqlCatalogSequence> _sequencesById = new();

    internal bool HasSequences
    { get { lock (_sync) { ThrowIfOffline(); return _sequences.Count != 0; } } }

    internal IReadOnlyList<SqlCatalogSequence> Sequences
    { get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_sequences.Values.Select(slot => slot.Value).Where(sequence => sequence.OwnerTableId == 0 || !_pendingReclamations.ContainsKey(sequence.OwnerTableId)).ToArray()); } } }

    internal SqlCatalogSequence? FindSequence(string schema, string name)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return _sequences.TryGetValue((schema, name), out var slot) && (slot.Value.OwnerTableId == 0 || !_pendingReclamations.ContainsKey(slot.Value.OwnerTableId)) ? slot.Value : null;
        }
    }

    internal SqlCatalogSequence? FindSequence(ulong objectId)
    {
        lock (_sync) { ThrowIfOffline(); return _sequencesById.TryGetValue(objectId, out var sequence) && (sequence.OwnerTableId == 0 || !_pendingReclamations.ContainsKey(sequence.OwnerTableId)) ? sequence : null; }
    }

    internal long AllocateSequence(ulong objectId)
    {
        var sequence = FindSequence(objectId) ?? throw new SqlCatalogException("CS04067", ErrorMessages.SequenceChanged);
        return AllocateSequence(sequence);
    }

    internal ValueTask<SqlCatalogSequence> CreateSequenceAsync(string schema, string name, SqlSequenceOptions options,
        CancellationToken cancellationToken = default, ulong ownerTableId = 0, string? ownerColumn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_namespaces.ContainsKey(schema)) { throw new SqlCatalogException("CS03071", ErrorMessages.SequenceNamespaceMissing); }
            if (RelationExists(schema, name)) { throw RelationNameCollision(schema, name); }
            var sequence = new SqlCatalogSequence(_nextObjectId, schema, name, options, ownerTableId, ownerColumn);
            byte[] record = EncodeSequence(sequence);
            using var transaction = _storage.BeginTransaction();
            var orphan = StageOrphanSequenceName(transaction, schema, name);
            var location = _storage.InsertRow(transaction, record);
            CommitCatalogChange(transaction, checked(_nextObjectId + 1));
            if (orphan is not null) { RemoveSequence(orphan); }
            _sequences.Add((schema, name), new(sequence, location));
            _sequencesById.Add(sequence.ObjectId, sequence);
            return ValueTask.FromResult(sequence);
        }
    }

    // Existence and option validation share the publication monitor. PostgreSQL
    // sequence.c:135-159 skips an occupied relation before init_params runs.
    internal ValueTask<SqlCatalogSequence?> CreateSequenceIfAbsentAsync(string schema, string name,
        Func<SqlSequenceOptions> options, bool ifNotExists, CancellationToken cancellationToken, Func<(ulong Table, string? Column)>? ownership = null)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (RelationExists(schema, name))
            {
                if (ifNotExists) { return ValueTask.FromResult<SqlCatalogSequence?>(null); }
                throw RelationNameCollision(schema, name);
            }
            var owner = ownership?.Invoke() ?? default;
            return ValueTask.FromResult<SqlCatalogSequence?>(CreateSequenceAsync(schema, name, options(), cancellationToken, owner.Table, owner.Column).Result);
        }
    }

    // The SQL DDL executor supplies resolved replacement options. Ordinary
    // replacement retains the last fetched value and discards its unused reservation;
    // explicit restart is transactional and may deliberately reissue old values.
    internal ValueTask<SqlCatalogSequence> AlterSequenceAsync(SqlCatalogSequence expected, SqlSequenceOptions options,
        long? restart = null, CancellationToken cancellationToken = default, (ulong Table, string? Column)? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(options);
        if (restart is { } first) { options.ValidateRestart(first); }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            var previous = RequireSequence(expected);
            bool ownershipOnly = ownership is not null && restart is null && ReferenceEquals(options, expected.Options);
            _sequenceCounters.TryGetValue(expected.ObjectId, out var oldCounter);
            bool called = restart is null && oldCounter is { IsCalled: true };
            // PostgreSQL sequence.c:479,513,1558-1570: ALTER validates and keeps
            // the fetched value, never the logged-ahead reservation (see :838).
            // After recovery Next - increment already equals the durable endpoint.
            long reserved = restart ?? (called ? (long)(oldCounter!.Next - expected.Options.Increment) :
                oldCounter?.Reserved ?? expected.Options.Start);
            options.ValidateRestart(reserved);
            var updated = new SqlCatalogSequence(expected.ObjectId, expected.Schema, expected.Name, options, ownership?.Table ?? expected.OwnerTableId, ownership is null ? expected.OwnerColumn : ownership.Value.Column, expected.IsIdentity);
            byte[] record = EncodeSequence(updated);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, previous.Location, record);
            var counterLocation = oldCounter?.Location;
            if (!ownershipOnly) { counterLocation = UpsertRecord(transaction, oldCounter?.Location,
                EncodeAllocation(sequenceCounterRecordKind, expected.ObjectId, reserved, called)); }
            CommitCatalogChange(transaction);
            _sequences[(expected.Schema, expected.Name)] = new(updated, location);
            _sequencesById[expected.ObjectId] = updated;
            if (updated.IsIdentity) { _identitySequences[updated.OwnerTableId] = updated; }
            if (!ownershipOnly) { _sequenceCounters[expected.ObjectId] = new SqlSequenceCounter
            { Next = RecoverAllocation(reserved, options.Increment, called), Reserved = reserved, IsCalled = called, Location = counterLocation }; }
            return ValueTask.FromResult(updated);
        }
    }

    internal ValueTask DropSequenceAsync(SqlCatalogSequence expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            var slot = RequireSequence(expected);
            EnsureSequenceDroppable(expected);
            using var transaction = _storage.BeginTransaction();
            StageSequenceDefaults(transaction, [expected], false);
            DeleteSequence(transaction, slot);
            CommitCatalogChange(transaction);
            _sequences.Remove((expected.Schema, expected.Name));
            _sequencesById.Remove(expected.ObjectId);
            _sequenceCounters.Remove(expected.ObjectId);
            return default;
        }
    }

    internal long AllocateSequence(SqlCatalogSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        AllocationEntering?.Invoke();
        lock (_sync)
        {
            return AllocateSequenceCore(sequence);
        }
    }

    // Caller owns _sync. Identity and standalone entry points share this allocator.
    private long AllocateSequenceCore(SqlCatalogSequence sequence)
    {
            ObjectDisposedException.ThrowIf(_allocationClosed, this);
            ThrowIfOffline();
            RequireSequence(sequence);
            var options = sequence.Options;
            if (!_sequenceCounters.TryGetValue(sequence.ObjectId, out var counter))
            {
                counter = new SqlSequenceCounter { Next = options.Start, Reserved = options.Start };
                _sequenceCounters.Add(sequence.ObjectId, counter);
            }
            if (!TryAllocate(counter, sequence.ObjectId, sequenceCounterRecordKind, options.Increment,
                options.Minimum, options.Maximum, options.Cycle, out long value))
            {
                if (sequence.IsIdentity)
                {
                    string type = options.Type switch { DatabaseType.Int8 => "TINYINT", DatabaseType.Int16 => "SMALLINT", DatabaseType.Int32 => "INT", _ => "BIGINT" };
                    throw new SqlCatalogException("CS02002", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdentityRangeExhausted,
                        sequence.OwnerColumn, type, options.Increment > 0 ? options.Maximum : options.Minimum));
                }
                throw new SqlCatalogException("CS02002", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SequenceExhausted, sequence.Schema, sequence.Name));
            }
            return value;
    }

    internal ValueTask DropSequencesAsync(IReadOnlyList<SqlCatalogSequence> sequences, CancellationToken cancellationToken, bool cascade = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfOffline();
            foreach (var sequence in sequences) { RequireSequence(sequence); EnsureSequenceDroppable(sequence); }
            using var transaction = _storage.BeginTransaction();
            var changed = StageSequenceDefaults(transaction, sequences, cascade);
            foreach (var sequence in sequences) { DeleteSequence(transaction, RequireSequence(sequence)); }
            CommitCatalogChange(transaction);
            foreach (var slot in changed) { _tables[(slot.Table.Schema, slot.Table.Name)] = slot; }
            foreach (var sequence in sequences)
            { _sequences.Remove((sequence.Schema, sequence.Name)); _sequencesById.Remove(sequence.ObjectId); _sequenceCounters.Remove(sequence.ObjectId); }
            return default;
        }
    }

    internal void SetSequence(SqlCatalogSequence sequence, long value, bool isCalled)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_allocationClosed, this);
            ThrowIfOffline();
            RequireSequence(sequence);
            if (value < sequence.Options.Minimum || value > sequence.Options.Maximum)
            { throw new SqlCatalogException("CS03075", ErrorMessages.SequenceSetvalBounds); }
            _sequenceCounters.TryGetValue(sequence.ObjectId, out var counter);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, counter?.Location,
                EncodeAllocation(sequenceCounterRecordKind, sequence.ObjectId, value, isCalled));
            ReservationCommitting?.Invoke();
            transaction.Commit();
            _sequenceCounters[sequence.ObjectId] = new SqlSequenceCounter
            { Next = RecoverAllocation(value, sequence.Options.Increment, isCalled), Reserved = value, IsCalled = isCalled, Location = location };
        }
    }

    private TableSlot[] StageSequenceDefaults(StorageTransaction transaction, IReadOnlyList<SqlCatalogSequence> sequences,
        bool cascade, IReadOnlySet<ulong>? removedTables = null, IReadOnlyDictionary<ulong, SqlCatalogTable>? overrides = null)
    {
        var dependencies = sequences.Select(sequence => "S:" + sequence.ObjectId.ToString(CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        var changed = new List<TableSlot>();
        foreach (var tableSlot in _tables.Values)
        {
            var table = overrides?.GetValueOrDefault(tableSlot.Table.ObjectId) ?? tableSlot.Table;
            if (removedTables?.Contains(table.ObjectId) == true) { continue; }
            var dependent = table.Columns.FirstOrDefault(column => column.DefaultFunctionCalls.Any(dependencies.Contains));
            if (dependent is null) { continue; }
            if (!cascade)
            {
                var sequence = sequences.First(sequence => dependent.DefaultFunctionCalls.Contains("S:" + sequence.ObjectId.ToString(CultureInfo.InvariantCulture)));
                throw new SqlCatalogException("CS03080", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SequenceDefaultDependent,
                    sequence.Schema, sequence.Name, table.Schema, table.Name, dependent.Name));
            }
            var columns = table.Columns.Select(column => column.DefaultFunctionCalls.Any(dependencies.Contains)
                ? new SqlCatalogColumn(column.Name, column.Type, column.IsNullable, null, column.Collation, column.DomainName,
                    [], column.Identity, column.MissingValueLiteral) : column).ToArray();
            var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, table.PrimaryKeyColumns,
                table.Owner, table.OwningSchema, table.Constraints, table.DroppedColumnOrdinals);
            changed.Add(new TableSlot(replacement, UpsertRecord(transaction, tableSlot.Location, EncodeTable(replacement))));
        }
        return changed.ToArray();
    }

    private void EnsureSequenceDroppable(SqlCatalogSequence sequence)
    {
        if (sequence.IsIdentity)
        {
            var table = FindTableByObjectId(sequence.OwnerTableId)!;
            throw new SqlCatalogException("CS03080", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SequenceIdentityDependent,
                sequence.Schema, sequence.Name, table.Schema, table.Name, sequence.OwnerColumn));
        }
    }

    internal bool RelationNameExists(string schema, string name)
    { lock (_sync) { ThrowIfOffline(); return RelationExists(schema, name); } }

    private bool RelationExists(string schema, string name)
        => _tables.ContainsKey((schema, name)) || _views.ContainsKey(SqlObjectIdentity.View(schema, name)) || SequenceNameExists(schema, name);

    // Prepared owners can still publish and reserve their names. Permanently abandoned
    // owners are invisible and may surrender their sequence metadata in the new owner's
    // publication transaction; their data pages and reclamation intent remain untouched.
    private bool IsOrphanSequence(SqlCatalogSequence sequence)
        => sequence.OwnerTableId != 0 && _pendingReclamations.ContainsKey(sequence.OwnerTableId) &&
            !_preparedTruncateOwners.Contains(sequence.OwnerTableId);

    private bool SequenceNameExists(string schema, string name)
        => _sequences.TryGetValue((schema, name), out var slot) && !IsOrphanSequence(slot.Value);

    private SqlCatalogSequence? StageOrphanSequenceName(StorageTransaction transaction, string schema, string name)
    {
        if (!_sequences.TryGetValue((schema, name), out var slot) || !IsOrphanSequence(slot.Value)) { return null; }
        DeleteSequence(transaction, slot);
        return slot.Value;
    }

    private SqlCatalogException RelationNameCollision(string schema, string name)
    {
        string kind = _sequences.ContainsKey((schema, name)) ? "Sequence" : _tables.ContainsKey((schema, name)) ? "Table" : "View";
        return new SqlCatalogException("CS03070", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SequenceRelationExists, kind, schema, name));
    }

    private DefinitionSlot<SqlCatalogSequence> RequireSequence(SqlCatalogSequence expected)
    {
        if (!_sequences.TryGetValue((expected.Schema, expected.Name), out var slot) || !ReferenceEquals(slot.Value, expected))
        { throw new SqlCatalogException("CS04067", ErrorMessages.SequenceChanged); }
        return slot;
    }

    private void DeleteSequence(StorageTransaction transaction, DefinitionSlot<SqlCatalogSequence> slot)
    {
        _storage.DeleteRow(transaction, slot.Location.PageId, slot.Location.SlotIndex);
        if (_sequenceCounters.TryGetValue(slot.Value.ObjectId, out var counter) && counter.Location is { } location)
        { _storage.DeleteRow(transaction, location.PageId, location.SlotIndex); }
    }

    private static byte[] EncodeSequence(SqlCatalogSequence sequence)
    {
        var options = sequence.Options;
        byte[] record = new DatabaseKeyWriter().AppendInt32(sequenceRecordKind).AppendInt64((long)sequence.ObjectId)
            .AppendString(sequence.Schema, Collation.Binary).AppendString(sequence.Name, Collation.Binary)
            .AppendInt32((int)options.Type).AppendInt64(options.Start).AppendInt64(options.Increment)
            .AppendInt64(options.Minimum).AppendInt64(options.Maximum).AppendInt64(options.Cache).AppendBoolean(options.Cycle)
            .AppendInt64((long)sequence.OwnerTableId).AppendString(sequence.OwnerColumn ?? "", Collation.Binary).AppendBoolean(sequence.IsIdentity).ToArray();
        if (record.Length > SlottedPage.MaxRecordSize) { throw new SqlCatalogException("CS03072", ErrorMessages.SequenceRecordTooLarge); }
        return record;
    }

    private void LoadSequence(ref DatabaseKeyReader reader, (PageId PageId, int SlotIndex) location)
    {
        ulong id = (ulong)reader.ReadInt64();
        string schema = reader.ReadString(out _), name = reader.ReadString(out _);
        int type = reader.ReadInt32();
        // DatabaseType has a byte backing type: refuse high bits before a cast can
        // silently turn damaged metadata into a supported integer type.
        if (type != (int)DatabaseType.Int8 && type != (int)DatabaseType.Int16 && type != (int)DatabaseType.Int32 && type != (int)DatabaseType.Int64)
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        long start = reader.ReadInt64(), increment = reader.ReadInt64(), minimum = reader.ReadInt64(), maximum = reader.ReadInt64(), cache = reader.ReadInt64();
        bool cycle = reader.ReadBoolean();
        ulong owner = (ulong)reader.ReadInt64();
        string column = reader.ReadString(out _);
        bool identity = reader.ReadBoolean();
        var options = new SqlSequenceOptions((DatabaseType)type, start, increment, minimum, maximum, cache, cycle, identity);
        if ((owner == 0) != (column.Length == 0) || identity && owner == 0) { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        if (!reader.IsAtEnd || id == 0 || id == ulong.MaxValue || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) ||
            !_sequences.TryAdd((schema, name), new(new SqlCatalogSequence(id, schema, name, options, owner, column.Length == 0 ? null : column, identity), location)))
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        _nextObjectId = Math.Max(_nextObjectId, id + 1);
    }

    private void LoadSequenceCounter(ref DatabaseKeyReader reader, (PageId PageId, int SlotIndex) location)
    {
        ulong id = (ulong)reader.ReadInt64();
        long reserved = reader.ReadInt64();
        bool called = reader.ReadBoolean();
        if (!reader.IsAtEnd || id == 0 || !_sequenceCounters.TryAdd(id,
            new SqlSequenceCounter { Reserved = reserved, IsCalled = called, Location = location }))
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
    }

    private void ValidateSequenceDirectory(IReadOnlyDictionary<ulong, SqlCatalogTable> tables)
    {
        var ids = new Dictionary<ulong, SqlCatalogSequence>();
        foreach (var slot in _sequences.Values)
        {
            var sequence = slot.Value;
            // Validate global identities before classifying provisional orphans. Cleanup
            // must never remove a live reservation through a colliding orphan id.
            if (!ids.TryAdd(sequence.ObjectId, sequence) || !_sequencesById.TryAdd(sequence.ObjectId, sequence) || tables.ContainsKey(sequence.ObjectId) || _pendingReclamations.ContainsKey(sequence.ObjectId) ||
                _tables.ContainsKey((sequence.Schema, sequence.Name)) || _views.ContainsKey(SqlObjectIdentity.View(sequence.Schema, sequence.Name)) ||
                sequence.OwnerTableId == 0 && !_namespaces.ContainsKey(sequence.Schema))
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
            if (sequence.OwnerTableId != 0 && (!tables.TryGetValue(sequence.OwnerTableId, out var owner) || !owner.Schema.Equals(sequence.Schema, StringComparison.OrdinalIgnoreCase) ||
                owner.FindColumn(sequence.OwnerColumn!) is not { } column || sequence.IsIdentity && column.Identity is null))
            {
                if (_pendingReclamations.ContainsKey(sequence.OwnerTableId)) { continue; }
                throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed);
            }
            if (sequence.IsIdentity && !_identitySequences.TryAdd(sequence.OwnerTableId, sequence)) { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        }
        foreach (var table in tables.Values)
        {
            foreach (var column in table.Columns)
            {
                if (column.Identity is not null && (!_identitySequences.TryGetValue(table.ObjectId, out var sequence) ||
                    !string.Equals(sequence.OwnerColumn, column.Name, StringComparison.OrdinalIgnoreCase)))
                { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
                foreach (var dependency in column.DefaultFunctionCalls.Where(value => value.StartsWith("S:", StringComparison.Ordinal)))
                { if (!ulong.TryParse(dependency.AsSpan(2), out ulong id) || FindSequence(id) is null) { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); } }
            }
        }
        foreach (var (id, counter) in _sequenceCounters)
        {
            if (!ids.TryGetValue(id, out var sequence) || counter.Reserved < sequence.Options.Minimum || counter.Reserved > sequence.Options.Maximum)
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
            counter.Next = RecoverAllocation(counter.Reserved, sequence.Options.Increment, counter.IsCalled);
        }
        // Loading is read-only. The published database's reclamation worker removes
        // invisible orphan sequences atomically with their retired owner's intent.
    }
}
