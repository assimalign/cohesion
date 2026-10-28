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

    internal IReadOnlyList<SqlCatalogSequence> Sequences
    { get { lock (_sync) { ThrowIfOffline(); return Array.AsReadOnly(_sequences.Values.Select(slot => slot.Value).ToArray()); } } }

    internal SqlCatalogSequence? FindSequence(string schema, string name)
    {
        lock (_sync)
        {
            ThrowIfOffline();
            return _sequences.TryGetValue((schema, name), out var slot) ? slot.Value : null;
        }
    }

    internal ValueTask<SqlCatalogSequence> CreateSequenceAsync(string schema, string name, SqlSequenceOptions options,
        CancellationToken cancellationToken = default)
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
            var sequence = new SqlCatalogSequence(_nextObjectId, schema, name, options);
            byte[] record = EncodeSequence(sequence);
            using var transaction = _storage.BeginTransaction();
            var location = _storage.InsertRow(transaction, record);
            CommitCatalogChange(transaction, checked(_nextObjectId + 1));
            _sequences.Add((schema, name), new(sequence, location));
            return ValueTask.FromResult(sequence);
        }
    }

    // Resolved replacement options are supplied by the future SQL binder. Ordinary
    // replacement retains the last fetched value and discards its unused reservation;
    // explicit restart is transactional and may deliberately reissue old values.
    internal ValueTask<SqlCatalogSequence> AlterSequenceAsync(SqlCatalogSequence expected, SqlSequenceOptions options,
        long? restart = null, CancellationToken cancellationToken = default)
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
            _sequenceCounters.TryGetValue(expected.ObjectId, out var oldCounter);
            bool called = restart is null && oldCounter is { IsCalled: true };
            // PostgreSQL sequence.c:479,513,1558-1570: ALTER validates and keeps
            // the fetched value, never the logged-ahead reservation (see :838).
            // After recovery Next - increment already equals the durable endpoint.
            long reserved = restart ?? (called ? (long)(oldCounter!.Next - expected.Options.Increment) :
                oldCounter?.Reserved ?? expected.Options.Start);
            options.ValidateRestart(reserved);
            var updated = new SqlCatalogSequence(expected.ObjectId, expected.Schema, expected.Name, options);
            byte[] record = EncodeSequence(updated);
            using var transaction = _storage.BeginTransaction();
            var location = UpsertRecord(transaction, previous.Location, record);
            var counterLocation = UpsertRecord(transaction, oldCounter?.Location,
                EncodeAllocation(sequenceCounterRecordKind, expected.ObjectId, reserved, called));
            CommitCatalogChange(transaction);
            _sequences[(expected.Schema, expected.Name)] = new(updated, location);
            _sequenceCounters[expected.ObjectId] = new SqlSequenceCounter
            { Next = RecoverAllocation(reserved, options.Increment, called), Reserved = reserved, IsCalled = called, Location = counterLocation };
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
            using var transaction = _storage.BeginTransaction();
            DeleteSequence(transaction, slot);
            CommitCatalogChange(transaction);
            _sequences.Remove((expected.Schema, expected.Name));
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
            { throw new SqlCatalogException("CS02002", string.Format(CultureInfo.CurrentCulture, ErrorMessages.SequenceExhausted, sequence.Schema, sequence.Name)); }
            return value;
        }
    }

    private bool RelationExists(string schema, string name)
        => _tables.ContainsKey((schema, name)) || _views.ContainsKey(SqlObjectIdentity.View(schema, name)) || _sequences.ContainsKey((schema, name));

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
            .AppendInt64(options.Minimum).AppendInt64(options.Maximum).AppendInt64(options.Cache).AppendBoolean(options.Cycle).ToArray();
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
        if (type != (int)DatabaseType.Int16 && type != (int)DatabaseType.Int32 && type != (int)DatabaseType.Int64)
        { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        var options = new SqlSequenceOptions((DatabaseType)type, reader.ReadInt64(), reader.ReadInt64(),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadBoolean());
        if (!reader.IsAtEnd || id == 0 || id == ulong.MaxValue || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) ||
            !_sequences.TryAdd((schema, name), new(new SqlCatalogSequence(id, schema, name, options), location)))
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
            if (!ids.TryAdd(sequence.ObjectId, sequence) || tables.ContainsKey(sequence.ObjectId) || _pendingReclamations.ContainsKey(sequence.ObjectId) ||
                _tables.ContainsKey((sequence.Schema, sequence.Name)) || _views.ContainsKey(SqlObjectIdentity.View(sequence.Schema, sequence.Name)) ||
                !_namespaces.ContainsKey(sequence.Schema))
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
        }
        foreach (var (id, counter) in _sequenceCounters)
        {
            if (!ids.TryGetValue(id, out var sequence) || counter.Reserved < sequence.Options.Minimum || counter.Reserved > sequence.Options.Maximum)
            { throw new SqlCatalogException("CS06061", ErrorMessages.SequenceRecordMalformed); }
            counter.Next = RecoverAllocation(counter.Reserved, sequence.Options.Increment, counter.IsCalled);
        }
    }
}
