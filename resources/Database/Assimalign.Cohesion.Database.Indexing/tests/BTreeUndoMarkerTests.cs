using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Indexing.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Indexing.Tests;

public sealed class BTreeUndoMarkerTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Indexing] - Undo: a second statement adapter cannot clear a pre-marker row and unique-index tombstone")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only reflection awaits the internal marker's ValueTask and reads its Task result; no shipped boundary is widened.")]
    public async Task RollbackToMarker_DuplicateThroughSecondBinding_ShouldKeepEarlierTombstone()
    {
        // Arrange: actual rows and a unique tree share the coordinator's physical brackets.
        using var storage = new UndoIndexTestStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var indexes = BTreeIndexManager.Create(new BTreeIndexManagerOptions
        {
            Storage = storage,
            TransactionSource = context => coordinator.TryGetStorageTransaction(context, out var bracket) ? bracket : throw new InvalidOperationException(),
            LockManager = coordinator.LockManager
        });
        var seeded = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var key = IndexKey.FromInt64(42);
        BTreeIndex? index = null;
        ulong row = 0;
        await coordinator.ApplyStatementAsync<int>(seeded, async bracket =>
        {
            index = await indexes.CreateIndexAsync(seeded, 1, new IndexDefinition("ix_unique", IndexKind.BTree, true));
            var record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, seeded.Sequence);
            var location = storage.Insert(bracket, record);
            row = storage.Records.PackLocation(location.PageId, location.SlotIndex);
            coordinator.VersionStore.RecordCreated(seeded.Sequence, location.PageId, location.SlotIndex);
            await index.InsertAsync(seeded, key, row);
            return 0;
        });
        await coordinator.CommitAsync(seeded);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var first = BTreeRecordVersionIndex.Create(index!);
        var second = BTreeRecordVersionIndex.Create(index!);
        var (page, slot) = storage.Records.UnpackLocation(row);
        await coordinator.ApplyStatementAsync<int>(writer, async bracket =>
        {
            storage.Records.Update(bracket, page, slot, RecordVersionStamp.WithDeleter(storage.Records.Read(page, slot).Span, writer.Sequence));
            coordinator.VersionStore.RecordTombstoned(writer.Sequence, page, slot);
            await index!.DeleteAsync(writer, key, row);
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, first, key.Encoded, row);
            return 0;
        });

        // Test-only reflection reaches the deliberately internal marker control without an
        // assembly grant, extra project reference, or new shipped API.
        var pendingMarker = typeof(TransactionCoordinator).GetMethod("CreateUndoMarkerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object[] { writer, CancellationToken.None })!;
        var task = (Task)pendingMarker.GetType().GetMethod("AsTask")!.Invoke(pendingMarker, null)!;
        await task;
        var marker = task.GetType().GetProperty("Result")!.GetValue(task)!;
        await coordinator.ApplyStatementAsync<int>(writer, async _ =>
        {
            await index!.DeleteAsync(writer, key, row); // physically a no-op
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, second, key.Encoded, row);
            return 0;
        });

        // Act
        var undo = (ValueTask)typeof(TransactionCoordinator).GetMethod("RollbackToUndoMarkerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(coordinator, new object[] { writer, marker, CancellationToken.None })!;
        await undo;
        await coordinator.CommitAsync(writer);

        // Assert: both the row and its tree entry remain deleted, so the key can be reused.
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0, row, reader.Snapshot)).HasValue.ShouldBeFalse();
        (await index!.FindLiveEntryAsync(reader, key)).ShouldBeNull();
        await coordinator.ApplyStatementAsync<int>(reader, async _ =>
        {
            await index.InsertAsync(reader, key, row + 1);
            return 0;
        });
        await coordinator.CommitAsync(reader);
    }
}
