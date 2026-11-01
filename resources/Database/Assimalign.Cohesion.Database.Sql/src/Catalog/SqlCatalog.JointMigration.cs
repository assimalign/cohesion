using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

public sealed partial class SqlCatalog
{
    internal Action? BeforeJointDiscard { get; set; }
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
        IReadOnlyList<SqlCatalogTable>? prepared = null)
    {
        BeforeJointDiscard?.Invoke();
        lock (_sync)
        {
            ThrowIfOffline();
            cancellationToken.ThrowIfCancellationRequested();
            if (_pendingMigration is null && prepared is not { Count: > 0 }) { return default; }
            using var transaction = _storage.BeginTransaction();
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
            _pendingMigrationLocations = [];
            _pendingMigration = null;
            return default;
        }
    }
}
