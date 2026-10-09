using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// Exercises record-space undo through real storage brackets and the shared
/// composition, including stale locations and retries after partial logical undo.
/// </summary>
public class RecordSpaceVersionStoreTests
{
    [Fact]
    public async Task PurgeWriter_FailureAfterCommittedBatches_RetriesIdempotently()
    {
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        for (int index = 0; index < 130; index++)
        {
            await coordinator.ApplyStatementAsync(writer, bracket =>
            {
                var bytes = new byte[8000];
                RecordVersionStamp.WriteWriter(bytes, writer.Sequence);
                var (page, slot) = storage.Insert(bracket, bytes);
                coordinator.VersionStore.RecordCreated(writer.Sequence, page, slot);
                return true;
            });
        }
        var failing = new FailingIndex();
        coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, failing, new byte[] { 1 }, 0);

        await Should.ThrowAsync<IOException>(() => coordinator.VersionStore.PurgeWriterAsync(writer.Sequence).AsTask());
        CountRecords(storage).ShouldBe(2);
        storage.PageManager.FreePageCount.ShouldBe(128);
        coordinator.VersionStore.PendingAbortedPurges.ShouldContain(writer.Sequence.Value);

        (await coordinator.VersionStore.PurgeWriterAsync(writer.Sequence)).ShouldBe(3);
        CountRecords(storage).ShouldBe(0);
        storage.PageManager.FreePageCount.ShouldBe(130);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        await coordinator.RollbackAsync(writer);
    }

    /// <summary>
    /// An index failure rolls record changes back and preserves copied ledger keys for retry.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record undo: failed index undo requeues complete ledger")]
    public async Task PurgeWriter_IndexUndoFails_ShouldRollbackAndRetryCopiedLedger()
    {
        // Arrange
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
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
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
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

    /// <summary>
    /// A version the snapshot cannot see reads as no version, not as an empty payload.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record visibility: an invisible version reads as null")]
    public async Task GetVisibleVersion_WriterInFlight_ShouldReturnNull()
    {
        // Arrange
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        (PageId PageId, int SlotIndex) location = default;
        await coordinator.ApplyStatementAsync(writer, bracket =>
        {
            location = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None, 7));
            coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }, CancellationToken.None);
        ulong packed = storage.PackLocation(location.PageId, location.SlotIndex);

        // Act
        var hidden = await coordinator.VersionStore.GetVisibleVersionAsync(0, packed, reader.Snapshot);
        var own = await coordinator.VersionStore.GetVisibleVersionAsync(0, packed, writer.Snapshot);

        // Assert
        hidden.HasValue.ShouldBeFalse();
        own.HasValue.ShouldBeTrue();
        own!.Value.Span[RecordVersionStamp.HeaderSize].ShouldBe((byte)7);
        await coordinator.RollbackAsync(writer, CancellationToken.None);
        await coordinator.CommitAsync(reader, CancellationToken.None);
    }

    /// <summary>
    /// A committed tombstone whose page cannot be read is not a reclaimed one (#1342): the prune
    /// fails and keeps the candidate for the next pass, where before the fix it read the failure
    /// as "already gone" and dropped the candidate, leaving the dead version on its page for good.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record prune: a version page that cannot be read fails the pass and keeps its candidate (#1342)")]
    public async Task Prune_VersionPageMalformed_ShouldFailAndKeepTheCandidate()
    {
        // Arrange: a committed tombstone below the bound, its slot entry then made to address
        // bytes past the page.
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        (PageId PageId, int SlotIndex) location = default;
        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            location = storage.Insert(bracket, Stamped(TransactionSequence.None, deleter.Sequence, 5));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }, CancellationToken.None);
        await coordinator.CommitAsync(deleter, CancellationToken.None);
        byte[] slotEntry;
        using (var handle = storage.PageManager.GetPage(location.PageId))
        {
            var entry = handle.Page.AsSpan().Slice(Page.Size - ((location.SlotIndex + 1) * 4), 4);
            slotEntry = entry.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(entry, Page.Size - 1);
        }

        // Act
        var failure = Should.Throw<StorageCorruptionException>(() => coordinator.RunVersionPurgePass(CancellationToken.None));
        int trackedAfterTheFailure = coordinator.VersionStore.TrackedVersionCount;
        using (var handle = storage.PageManager.GetPage(location.PageId))
        {
            slotEntry.CopyTo(handle.Page.AsSpan().Slice(Page.Size - ((location.SlotIndex + 1) * 4), 4));
        }

        long pruned = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        failure.PageId.ShouldBe(location.PageId);
        trackedAfterTheFailure.ShouldBe(1);
        pruned.ShouldBe(1);
        CountRecords(storage).ShouldBe(0);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    /// <summary>
    /// One unreadable version page must not stop reclamation for the whole record space. Before the
    /// fix the prune threw at the first candidate it could not read: the batch bracket holding the
    /// readable candidates rolled back, the batches after it never ran, and every later pass met the
    /// same page first.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record prune: an unreadable version page keeps its candidate and the pass still reclaims the others (#1342)")]
    public async Task Prune_OneVersionPageMalformed_ShouldReclaimTheOthersAndKeepIt()
    {
        // Arrange: two committed tombstones on pages of their own, the first one's slot entry then
        // made to address bytes past its page.
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var locations = new List<(PageId PageId, int SlotIndex)>();
        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            for (int index = 0; index < 2; index++)
            {
                var record = new byte[8000];
                RecordVersionStamp.WriteWriter(record, TransactionSequence.None);
                var location = storage.Insert(bracket, RecordVersionStamp.WithDeleter(record, deleter.Sequence));
                coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
                locations.Add(location);
            }

            return 0;
        }, CancellationToken.None);
        await coordinator.CommitAsync(deleter, CancellationToken.None);
        var (unreadable, readable) = (locations[0], locations[1]);
        readable.PageId.ShouldNotBe(unreadable.PageId);
        byte[] slotEntry = Malform(storage, unreadable);

        // Act
        var failure = Should.Throw<StorageCorruptionException>(() => coordinator.RunVersionPurgePass(CancellationToken.None));
        bool readableReclaimed = !storage.FreeSpaceMap.IsAllocated(readable.PageId);
        int trackedAfterTheFailure = coordinator.VersionStore.TrackedVersionCount;
        Restore(storage, unreadable, slotEntry);
        long prunedOnceReadable = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        failure.PageId.ShouldBe(unreadable.PageId);
        readableReclaimed.ShouldBeTrue();
        trackedAfterTheFailure.ShouldBe(1);
        prunedOnceReadable.ShouldBe(1);
        CountRecords(storage).ShouldBe(0);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(0);
    }

    /// <summary>
    /// A writer queued by a direct <see cref="VersionStore.PurgeWriterAsync"/> caller whose undo keeps
    /// failing does not stop the prune: before the fix the pass threw at the queued writer and never
    /// reached the committed tombstones.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record prune: a queued undo that fails again is rethrown after the prune ran (#1342)")]
    public async Task RunVersionPurgePass_QueuedUndoFailsAgain_ShouldStillPrune()
    {
        // Arrange: a committed tombstone below the bound, then a writer whose direct undo failed and
        // whose retry will fail once more.
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            var location = storage.Insert(bracket, Stamped(TransactionSequence.None, deleter.Sequence, 5));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }, CancellationToken.None);
        await coordinator.CommitAsync(deleter, CancellationToken.None);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var failing = new FailingIndex(clearFailures: 2);
        coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, failing, new byte[] { 1 }, 0);
        await Should.ThrowAsync<IOException>(() => coordinator.VersionStore.PurgeWriterAsync(writer.Sequence).AsTask());

        // Act
        Should.Throw<IOException>(() => coordinator.RunVersionPurgePass(CancellationToken.None));
        int recordsAfterTheFailedPass = CountRecords(storage);
        var pendingAfterTheFailedPass = coordinator.VersionStore.PendingAbortedPurges.ToArray();
        coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert: the tombstone was reclaimed by the pass that failed; the writer waited for the next.
        recordsAfterTheFailedPass.ShouldBe(0);
        pendingAfterTheFailedPass.ShouldBe([writer.Sequence.Value]);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        failing.Calls.Count(call => call.Operation == "clear").ShouldBe(3);
        await coordinator.RollbackAsync(writer, CancellationToken.None);
    }

    /// <summary>
    /// A transaction the manager has begun is covered by the prune bound from the moment its
    /// snapshot exists. Before the fix the bound started from the oldest active sequence and was
    /// lowered only by the coordinator's open transactions, which a new transaction joins after the
    /// manager began it: a purge in between reclaimed a version the new snapshot still saw. The
    /// reader here is begun on the manager alone, which is that window's state, held open.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record prune: a snapshot the manager began, not yet tracked by the coordinator, keeps the version it sees")]
    public async Task Prune_SnapshotBegunOnTheManagerAlone_ShouldKeepTheVersionItSees()
    {
        // Arrange: a version tombstoned by a writer still in flight when the reader begins, then
        // the writer commits.
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong packed = await TombstoneOneVersion(storage, coordinator, deleter);
        var reader = await coordinator.Manager.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        await coordinator.CommitAsync(deleter, CancellationToken.None);

        // Act
        long pruned = coordinator.RunVersionPurgePass(CancellationToken.None);
        var seen = await coordinator.VersionStore.GetVisibleVersionAsync(0, packed, reader.Snapshot);
        await coordinator.Manager.CommitAsync(reader, CancellationToken.None);
        long prunedAfterTheReader = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert: the reader saw the deleter in flight, so the version stayed until it ended.
        reader.Snapshot.Minimum.ShouldBe(deleter.Sequence);
        pruned.ShouldBe(0);
        seen.HasValue.ShouldBeTrue();
        prunedAfterTheReader.ShouldBe(1);
    }

    /// <summary>
    /// A read-committed statement pins a snapshot the manager does not track, and the transaction's
    /// own snapshot moves on with every access. Before the fix the bound read only the latter, so a
    /// purge during the statement reclaimed a version its pinned snapshot still saw.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Record prune: a read-committed statement's pinned snapshot keeps the version it sees")]
    public async Task Prune_ReadCommittedStatementView_ShouldKeepTheVersionItsSnapshotSees()
    {
        // Arrange: the statement pins its snapshot while the deleter is in flight; the deleter
        // then commits, so the transaction's own snapshot no longer sees the version.
        using var storage = new RecordStorage();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong packed = await TombstoneOneVersion(storage, coordinator, deleter);
        var reader = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
        var statement = reader.PinStatementSnapshot();
        await coordinator.CommitAsync(deleter, CancellationToken.None);

        // Act
        long pruned = coordinator.RunVersionPurgePass(CancellationToken.None);
        var seen = await coordinator.VersionStore.GetVisibleVersionAsync(0, packed, statement.Snapshot);
        var seenByTheTransaction = await coordinator.VersionStore.GetVisibleVersionAsync(0, packed, reader.Snapshot);
        await coordinator.CommitAsync(reader, CancellationToken.None);
        long prunedAfterTheReader = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        statement.Snapshot.Minimum.ShouldBe(deleter.Sequence);
        pruned.ShouldBe(0);
        seen.HasValue.ShouldBeTrue();
        seenByTheTransaction.HasValue.ShouldBeFalse();
        prunedAfterTheReader.ShouldBe(1);
    }

    /// <summary>
    /// Writes one version, visible to every snapshot, that <paramref name="deleter"/> tombstones,
    /// and returns its packed location.
    /// </summary>
    private static async Task<ulong> TombstoneOneVersion(RecordStorage storage, TransactionCoordinator coordinator, TransactionContext deleter)
    {
        (PageId PageId, int SlotIndex) location = default;
        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            location = storage.Insert(bracket, Stamped(TransactionSequence.None, deleter.Sequence, 5));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }, CancellationToken.None);
        return storage.PackLocation(location.PageId, location.SlotIndex);
    }

    private static byte[] Malform(RecordStorage storage, (PageId PageId, int SlotIndex) location)
    {
        using var handle = storage.PageManager.GetPage(location.PageId);
        var entry = handle.Page.AsSpan().Slice(Page.Size - ((location.SlotIndex + 1) * 4), 4);
        byte[] original = entry.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(entry, Page.Size - 1);
        return original;
    }

    private static void Restore(RecordStorage storage, (PageId PageId, int SlotIndex) location, byte[] slotEntry)
    {
        using var handle = storage.PageManager.GetPage(location.PageId);
        slotEntry.CopyTo(handle.Page.AsSpan().Slice(Page.Size - ((location.SlotIndex + 1) * 4), 4));
    }

    private static byte[] Stamped(TransactionSequence writer, TransactionSequence deleter, byte payload)
    {
        byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
        RecordVersionStamp.WriteWriter(record, writer);
        record[16] = payload;
        return RecordVersionStamp.WithDeleter(record, deleter);
    }

    private static int CountRecords(Storage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    private sealed class FailingIndex : RecordVersionIndex
    {
        private int _clearFailures;

        internal FailingIndex(int clearFailures = 1)
        {
            _clearFailures = clearFailures;
        }

        internal List<(string Operation, byte[] Key, ulong EntryReference, TransactionSequence Writer)> Calls { get; } = new();

        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.IsActive.ShouldBeTrue();
            Calls.Add(("erase", key.ToArray(), entryReference, writer));
            return default;
        }

        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            transaction.IsActive.ShouldBeTrue();
            Calls.Add(("clear", key.ToArray(), entryReference, writer));
            if (_clearFailures > 0)
            {
                _clearFailures--;
                throw new IOException("Injected index undo failure.");
            }

            return default;
        }
    }

    // Only the model's record access is adapted; WAL brackets and slotted records are real.
    private sealed class RecordStorage : Assimalign.Cohesion.Database.Storage.Storage
    {
        internal RecordStorage()
            : base(StorageModel.Custom, new StorageStream(new SimulatedDurableFileHandle()), new StorageStream(new SimulatedDurableFileHandle()), StorageStream.FromInMemory())
        {
            InitializeNew((Name)"record-version-test");
        }

        internal StorageJournal Log => WriteAheadLog;

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction transaction, ReadOnlySpan<byte> record)
            => InsertRecord(transaction, record);

        internal ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        internal void Update(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(transaction, pageId, slotIndex, record);

        internal void Delete(StorageTransaction transaction, PageId pageId, int slotIndex)
            => DeleteRecord(transaction, pageId, slotIndex);

        internal ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        internal (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

        /// <summary>
        /// Gets the coordinator's record space over this storage's records. The double used to be
        /// the record space itself; both are abstract classes now, so it is split (plan C9).
        /// </summary>
        internal TransactionRecordSpace Records => _records ??= new RecordSpace(this);

        private RecordSpace? _records;

        private sealed class RecordSpace : TransactionRecordSpace
        {
            private readonly RecordStorage _storage;

            internal RecordSpace(RecordStorage storage)
            {
                _storage = storage;
            }

            protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.Read(pageId, slotIndex);

            protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
                => _storage.Update(transaction, pageId, slotIndex, record);

            protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
                => _storage.Delete(transaction, pageId, slotIndex);

            protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _storage.PackLocation(pageId, slotIndex);

            protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _storage.UnpackLocation(location);
        }
    }
}
