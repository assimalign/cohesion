using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// Exercises record-space undo through real storage brackets and the shared
/// composition, including stale locations and retries after partial logical undo.
/// </summary>
public class RecordSpaceVersionStoreTests
{
    /// <summary>
    /// An index failure rolls record changes back and preserves copied ledger keys for retry.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record undo: failed index undo requeues complete ledger")]
    public async Task PurgeWriter_IndexUndoFails_ShouldRollbackAndRetryCopiedLedger()
    {
        // Arrange
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var versions = coordinator.VersionStore;
        var index = new FailingIndex();
        byte[] key = [1, 2, 3];
        (PageId PageId, int SlotIndex) oldLocation = default;
        (PageId PageId, int SlotIndex) newLocation = default;

        await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            oldLocation = storage.Insert(bracket, Stamped(TransactionSequence.None, writer.Sequence, 11));
            newLocation = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None, 22));
            versions.RecordTombstoned(writer.Sequence, oldLocation.PageId, oldLocation.SlotIndex);
            versions.RecordCreated(writer.Sequence, newLocation.PageId, newLocation.SlotIndex);
            versions.RecordIndexEntryCreated(writer.Sequence, index, key, storage.PackLocation(newLocation.PageId, newLocation.SlotIndex));
            versions.RecordIndexEntryTombstoned(writer.Sequence, index, key, storage.PackLocation(oldLocation.PageId, oldLocation.SlotIndex));
            return 0;
        }, CancellationToken.None);
        key[0] = 99;

        // Act: the last operation fails after both record undo mutations ran.
        await Should.ThrowAsync<IOException>(() => versions.PurgeWriterAsync(writer.Sequence, CancellationToken.None).AsTask());

        // Assert: disposal of the failed bracket restored both physical records.
        versions.TrackedVersionCount.ShouldBe(4);
        versions.PendingAbortedPurges.ShouldContain(writer.Sequence.Value);
        RecordVersionStamp.ReadStamps(storage.Read(oldLocation.PageId, oldLocation.SlotIndex).Span).Deleter.ShouldBe(writer.Sequence);
        RecordVersionStamp.ReadStamps(storage.Read(newLocation.PageId, newLocation.SlotIndex).Span).Writer.ShouldBe(writer.Sequence);

        // Act: the same ledger now completes, including its copied index keys.
        long removed = await versions.PurgeWriterAsync(writer.Sequence, CancellationToken.None);

        // Assert
        removed.ShouldBe(4);
        versions.TrackedVersionCount.ShouldBe(0);
        versions.PendingAbortedPurges.ShouldBeEmpty();
        RecordVersionStamp.ReadStamps(storage.Read(oldLocation.PageId, oldLocation.SlotIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
        CountRecords(storage).ShouldBe(1);
        index.Calls.Select(call => call.Operation).ShouldBe(new[] { "erase", "clear", "erase", "clear" });
        foreach (var call in index.Calls)
        {
            call.Key.ShouldBe(new byte[] { 1, 2, 3 });
            call.Writer.ShouldBe(writer.Sequence);
            call.EntryReference.ShouldBe(call.Operation == "erase"
                ? storage.PackLocation(newLocation.PageId, newLocation.SlotIndex)
                : storage.PackLocation(oldLocation.PageId, oldLocation.SlotIndex));
        }

        await coordinator.RollbackAsync(writer, CancellationToken.None);
    }

    /// <summary>
    /// A failed statement's ledger can reference a reverted slot or another writer's replacement.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Record undo: failed statement ledger cannot delete replacement")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeWriter_FailedStatementLeavesStaleLocation_ShouldPreserveOtherVersions(bool reuseSlot)
    {
        // Arrange
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        (PageId PageId, int SlotIndex) revertedLocation = default;

        await Should.ThrowAsync<IOException>(() => coordinator.ApplyStatementAsync<int>(writer, bracket =>
        {
            revertedLocation = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None, 1));
            coordinator.VersionStore.RecordCreated(writer.Sequence, revertedLocation.PageId, revertedLocation.SlotIndex);
            throw new IOException("Injected statement failure.");
        }, CancellationToken.None).AsTask());
        CountRecords(storage).ShouldBe(0);
        coordinator.PairedTransactionCount.ShouldBe(0);

        if (reuseSlot)
        {
            var replacement = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
            await coordinator.ApplyStatementAsync(replacement, bracket =>
            {
                var location = storage.Insert(bracket, Stamped(replacement.Sequence, TransactionSequence.None, 2));
                location.ShouldBe(revertedLocation);
                coordinator.VersionStore.RecordCreated(replacement.Sequence, location.PageId, location.SlotIndex);
                return 0;
            }, CancellationToken.None);
            await coordinator.CommitAsync(replacement, CancellationToken.None);
        }

        // Act
        long removed = await coordinator.VersionStore.PurgeWriterAsync(writer.Sequence, CancellationToken.None);

        // Assert
        removed.ShouldBe(0);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        CountRecords(storage).ShouldBe(reuseSlot ? 1 : 0);
        if (reuseSlot)
        {
            storage.Read(revertedLocation.PageId, revertedLocation.SlotIndex).Span[16].ShouldBe((byte)2);
        }

        await coordinator.RollbackAsync(writer, CancellationToken.None);
    }

    private static byte[] Stamped(TransactionSequence writer, TransactionSequence deleter, byte payload)
    {
        byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
        RecordVersionStamp.WriteWriter(record, writer);
        record[16] = payload;
        return RecordVersionStamp.WithDeleter(record, deleter);
    }

    private static int CountRecords(IStorage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    private sealed class FailingIndex : IRecordVersionIndex
    {
        private bool _failClear = true;

        internal List<(string Operation, byte[] Key, ulong EntryReference, TransactionSequence Writer)> Calls { get; } = new();

        public ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.IsActive.ShouldBeTrue();
            Calls.Add(("erase", key.ToArray(), entryReference, writer));
            return default;
        }

        public ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.IsActive.ShouldBeTrue();
            Calls.Add(("clear", key.ToArray(), entryReference, writer));
            if (_failClear)
            {
                _failClear = false;
                throw new IOException("Injected index undo failure.");
            }

            return default;
        }
    }

    // Only the model's record access is adapted; WAL brackets and slotted records are real.
    private sealed class RecordStorage : Assimalign.Cohesion.Database.Storage.Storage, ITransactionRecordSpace
    {
        internal RecordStorage()
            : base(StorageStream.FromInMemory(), StorageStream.FromInMemory(), StorageStream.FromInMemory())
        {
            InitializeNew((Name)"record-version-test");
        }

        public override StorageModel Model => StorageModel.Custom;

        internal IStorageJournal Log => WriteAheadLog;

        internal (PageId PageId, int SlotIndex) Insert(IStorageTransaction transaction, ReadOnlySpan<byte> record)
            => InsertRecord(transaction, record);

        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        public void Update(IStorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(transaction, pageId, slotIndex, record);

        public void Delete(IStorageTransaction transaction, PageId pageId, int slotIndex)
            => DeleteRecord(transaction, pageId, slotIndex);

        public ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        public (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));
    }
}
