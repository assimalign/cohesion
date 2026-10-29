using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    private readonly Dictionary<ulong, SqlCatalogSequence> _identitySequences = new();
    private ulong _sequenceDefaultGeneration = ulong.MaxValue;
    private bool _hasSequenceDefaults;

    internal bool HasSequenceDefaults
    {
        get
        {
            lock (_sync)
            {
                ThrowIfOffline();
                if (_sequences.Count == 0) { return false; }
                if (_sequenceDefaultGeneration == _ddlGeneration) { return _hasSequenceDefaults; }
                _hasSequenceDefaults = false;
                foreach (var slot in _tables.Values)
                {
                    foreach (var column in slot.Table.Columns)
                    {
                        if (column.Identity is not null || column.DefaultFunctionCalls.Any(static call => call.StartsWith("S:", StringComparison.Ordinal)))
                        { _hasSequenceDefaults = true; break; }
                    }
                    if (_hasSequenceDefaults) { break; }
                }
                _sequenceDefaultGeneration = _ddlGeneration;
                return _hasSequenceDefaults;
            }
        }
    }
    /// <summary>Allocates through the column's owned sequence.</summary>
    /// <param name="table">The current table description.</param>
    /// <returns>The next generated value.</returns>
    /// <exception cref="ArgumentNullException">The table is null.</exception>
    /// <exception cref="SqlCatalogException">The table is stale, has no identity, or its sequence is exhausted.</exception>
    /// <exception cref="StorageException">The reservation cannot be persisted.</exception>
    /// <exception cref="ObjectDisposedException">Allocation has closed.</exception>
    public long AllocateIdentity(SqlCatalogTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        AllocationEntering?.Invoke();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_allocationClosed, this);
            ThrowIfOffline();
            if (!_tables.TryGetValue((table.Schema, table.Name), out var current) || !ReferenceEquals(current.Table, table))
            { throw new SqlCatalogException("CS04064", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdentityTableChanged, table.Schema, table.Name)); }
            if (!_identitySequences.TryGetValue(table.ObjectId, out var sequence))
            { throw new SqlCatalogException("CS01015", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdentityColumnMissing, table.Schema, table.Name)); }
            return AllocateSequenceCore(sequence);
        }
    }

    // Avoid allocating a table snapshot/closure on sequence-free DML without references.
    internal bool HasIncomingReferences(SqlCatalogTable table, bool changingUpdates = false)
    {
        lock (_sync)
        {
            foreach (var slot in _tables.Values)
            {
                for (int index = 0; index < slot.Table.Constraints.Count; index++)
                {
                    var constraint = slot.Table.Constraints[index];
                    if (constraint.Kind == SqlCatalogConstraintKind.Reference &&
                        (!changingUpdates || constraint.OnUpdate is SqlCatalogReferentialAction.Cascade or SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault) &&
                        string.Equals(constraint.ReferencedSchema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(constraint.ReferencedTable, table.Name, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
            }
            return false;
        }
    }

    internal SqlCatalogSequence IdentitySequence(ulong tableId, string column)
    {
        lock (_sync)
        {
            if (_identitySequences.TryGetValue(tableId, out var sequence) && string.Equals(sequence.OwnerColumn, column, StringComparison.OrdinalIgnoreCase)) { return sequence; }
            throw new SqlCatalogException("CS04067", ErrorMessages.SequenceChanged);
        }
    }

    // All callers hold the catalog monitor and publish this staged directory only after
    // their table transaction commits. Sequence ids are reserved in the same transaction.
    private Action StageOwnedSequences(StorageTransaction transaction, SqlCatalogTable? previous, SqlCatalogTable? updated,
        ref ulong nextId, bool restart = false, string? renamedColumn = null, string? newColumn = null)
    {
        // Publication holds the catalog monitor: a sequence may have disappeared after
        // binding or while its Shared grant waited. Never persist a dangling default.
        if (updated is not null)
        {
            foreach (var column in updated.Columns)
            {
                foreach (string dependency in column.DefaultFunctionCalls)
                {
                    if (dependency.StartsWith("S:", StringComparison.Ordinal) &&
                        (!ulong.TryParse(dependency.AsSpan(2), out ulong id) || FindSequence(id) is null))
                    { throw new SqlCatalogException("CS03073", ErrorMessages.SequenceMissing); }
                }
            }
        }
        bool hasIdentity = false;
        if (updated is not null)
        { for (int i = 0; i < updated.Columns.Count; i++) { if (updated.Columns[i].Identity is not null) { hasIdentity = true; break; } } }
        bool hasOwned = false;
        if (previous is not null && _sequences.Count != 0)
        { foreach (var slot in _sequences.Values) { if (slot.Value.OwnerTableId == previous.ObjectId) { hasOwned = true; break; } } }
        var nameOrphan = updated is null ? null : StageOrphanSequenceName(transaction, updated.Schema, updated.Name);
        if (!hasIdentity && !hasOwned && nameOrphan is null) { return static () => { }; }
        var removed = new List<SqlCatalogSequence>();
        if (nameOrphan is not null) { removed.Add(nameOrphan); }
        var staged = new List<DefinitionSlot<SqlCatalogSequence>>();
        var reset = new List<(ulong Id, SqlSequenceCounter Counter)>();
        if (previous is not null)
        {
            foreach (var slot in _sequences.Values.Where(slot => slot.Value.OwnerTableId == previous.ObjectId).ToArray())
            {
                var sequence = slot.Value;
                string name = renamedColumn is not null && string.Equals(sequence.OwnerColumn, renamedColumn, StringComparison.OrdinalIgnoreCase)
                    ? newColumn! : sequence.OwnerColumn!;
                var column = updated?.FindColumn(name);
                if (column is null || sequence.IsIdentity && column.Identity is null)
                { DeleteSequence(transaction, slot); removed.Add(sequence); continue; }
                var options = sequence.Options;
                if (sequence.IsIdentity && previous.FindColumn(sequence.OwnerColumn!)?.Type.Type != column.Type.Type)
                {
                    var oldBounds = SqlSequenceOptions.IntegerBounds(options.Type, identity: true);
                    var newBounds = SqlSequenceOptions.IntegerBounds(column.Type.Type, identity: true);
                    options = new SqlSequenceOptions(column.Type.Type, options.Start, options.Increment,
                        options.Minimum == oldBounds.Minimum ? newBounds.Minimum : options.Minimum,
                        options.Maximum == oldBounds.Maximum ? newBounds.Maximum : options.Maximum, options.Cache, options.Cycle, identity: true);
                    if (_sequenceCounters.TryGetValue(sequence.ObjectId, out var counter) && counter.IsCalled)
                    {
                        long last = checked((long)(counter.Next - sequence.Options.Increment));
                        if (last < options.Minimum || last > options.Maximum)
                        { throw new SqlCatalogException("CS02002", string.Format(CultureInfo.CurrentCulture, ErrorMessages.IdentityReplacementRange, column.Name, column.Type.Type)); }
                    }
                }
                var replacement = new SqlCatalogSequence(sequence.ObjectId, sequence.Schema, sequence.Name, options,
                    updated!.ObjectId, name, sequence.IsIdentity);
                staged.Add(new(replacement, UpsertRecord(transaction, slot.Location, EncodeSequence(replacement))));
                if (restart || !ReferenceEquals(options, sequence.Options))
                {
                    _sequenceCounters.TryGetValue(sequence.ObjectId, out var previousCounter);
                    bool called = !restart && previousCounter is { IsCalled: true };
                    long reserved = restart ? options.Start : called ? checked((long)(previousCounter!.Next - sequence.Options.Increment))
                        : previousCounter?.Reserved ?? sequence.Options.Start;
                    options.ValidateRestart(reserved);
                    var location = UpsertRecord(transaction, previousCounter?.Location,
                        EncodeAllocation(sequenceCounterRecordKind, sequence.ObjectId, reserved, called));
                    reset.Add((sequence.ObjectId, new SqlSequenceCounter { Next = RecoverAllocation(reserved, options.Increment, called), Reserved = reserved, IsCalled = called, Location = location }));
                }
            }
        }
        if (updated is not null)
        {
            foreach (var column in updated.Columns.Where(column => column.Identity is not null))
            {
                if (staged.Any(slot => slot.Value.IsIdentity && string.Equals(slot.Value.OwnerColumn, column.Name, StringComparison.OrdinalIgnoreCase)) ||
                    _sequencesById.Values.Any(sequence => sequence.IsIdentity && sequence.OwnerTableId == updated.ObjectId &&
                        string.Equals(sequence.OwnerColumn, column.Name, StringComparison.OrdinalIgnoreCase))) { continue; }
                var identity = column.Identity!;
                var options = identity.Options ?? new SqlSequenceOptions(column.Type.Type, identity.Start, identity.Increment, cache: 1, identity: true);
                string stem = updated.Name + "_" + column.Name + "_seq";
                string name = stem;
                int suffix = 0;
                while (RelationExists(updated.Schema, name) || name.Equals(updated.Name, StringComparison.OrdinalIgnoreCase) || staged.Any(slot => slot.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { name = stem + (++suffix).ToString(CultureInfo.InvariantCulture); }
                var orphan = StageOrphanSequenceName(transaction, updated.Schema, name);
                if (orphan is not null) { removed.Add(orphan); }
                var sequence = new SqlCatalogSequence(checked(nextId++), updated.Schema, name, options, updated.ObjectId, column.Name, identity: true);
                staged.Add(new(sequence, _storage.InsertRow(transaction, EncodeSequence(sequence))));
            }
        }
        if (removed.Count != 0)
        {
            StageSequenceDefaults(transaction, removed, false,
                updated is null && previous is not null ? new HashSet<ulong> { previous.ObjectId } : null,
                updated is null ? null : new Dictionary<ulong, SqlCatalogTable> { [updated.ObjectId] = updated });
        }
        return () =>
        {
            foreach (var sequence in removed) { RemoveSequence(sequence); }
            foreach (var slot in staged) { _sequences[(slot.Value.Schema, slot.Value.Name)] = slot; _sequencesById[slot.Value.ObjectId] = slot.Value; if (slot.Value.IsIdentity) { _identitySequences.Remove(previous?.ObjectId ?? 0); _identitySequences[slot.Value.OwnerTableId] = slot.Value; } }
            foreach (var item in reset) { _sequenceCounters[item.Id] = item.Counter; }
        };
    }

    internal System.Threading.Tasks.ValueTask ChangeIdentityAsync(SqlCatalogTable expected, SqlCatalogTable updated,
        SqlSequenceOptions? options, long? restart, SqlCatalogScriptPublication? publication, System.Threading.CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            ThrowIfOffline(); cancellationToken.ThrowIfCancellationRequested();
            var slot = GetSlot(expected.Schema, expected.Name);
            if (!ReferenceEquals(slot.Table, expected)) { throw new SqlCatalogException("CS04064", ErrorMessages.SequenceChanged); }
            ValidateColumns(updated.Schema, updated.Name, updated.Columns, updated.PrimaryKeyColumns);
            using var transaction = _storage.BeginTransaction();
            ulong next = _nextObjectId;
            var owned = StageOwnedSequences(transaction, expected, updated, ref next);
            var location = UpsertRecord(transaction, slot.Location, EncodeTable(updated));
            SqlCatalogSequence? replacement = null;
            SqlSequenceCounter? counter = null;
            DefinitionSlot<SqlCatalogSequence>? sequenceSlot = null;
            var old = _sequencesById.Values.FirstOrDefault(sequence => sequence.IsIdentity && sequence.OwnerTableId == expected.ObjectId);
            if (old is not null && updated.Columns.Any(column => column.Identity is not null) && options is not null)
            {
                _sequenceCounters.TryGetValue(old.ObjectId, out var previous);
                bool called = restart is null && previous is { IsCalled: true };
                long reserved = restart ?? (called ? checked((long)(previous!.Next - old.Options.Increment)) : previous?.Reserved ?? old.Options.Start);
                options.ValidateRestart(reserved);
                replacement = new SqlCatalogSequence(old.ObjectId, old.Schema, old.Name, options, old.OwnerTableId, old.OwnerColumn, true);
                sequenceSlot = new(replacement, UpsertRecord(transaction, RequireSequence(old).Location, EncodeSequence(replacement)));
                var counterLocation = UpsertRecord(transaction, previous?.Location, EncodeAllocation(sequenceCounterRecordKind, old.ObjectId, reserved, called));
                counter = new SqlSequenceCounter { Next = RecoverAllocation(reserved, options.Increment, called), Reserved = reserved, IsCalled = called, Location = counterLocation };
            }
            CommitCatalogChange(transaction, next, publication, actualObject: SqlObjectIdentity.Table(updated.Schema, updated.Name));
            owned();
            if (replacement is not null)
            { _sequences[(replacement.Schema, replacement.Name)] = sequenceSlot!; _sequencesById[replacement.ObjectId] = replacement; _sequenceCounters[replacement.ObjectId] = counter!; _identitySequences[replacement.OwnerTableId] = replacement; }
            _tables[(updated.Schema, updated.Name)] = new TableSlot(updated, location);
            return default;
        }
    }

    private void RemoveSequence(SqlCatalogSequence sequence)
    { if (sequence.IsIdentity) { _identitySequences.Remove(sequence.OwnerTableId); } _sequences.Remove((sequence.Schema, sequence.Name)); _sequencesById.Remove(sequence.ObjectId); _sequenceCounters.Remove(sequence.ObjectId); }
}
