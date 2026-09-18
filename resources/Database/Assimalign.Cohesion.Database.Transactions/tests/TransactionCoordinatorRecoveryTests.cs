using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Tests;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Exercises the shared coordinator's recovery ordering and snapshot purge bound
/// against real record pages and a real write-ahead journal.
/// </summary>
public class TransactionCoordinatorRecoveryTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: lifecycle appends cannot interleave with checkpoint truncation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Checkpoint_ConcurrentLifecycleAppend_ShouldPreserveClassification(bool commit)
    {
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var active = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var checkpointEntered = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var lifecycleStarted = new ManualResetEventSlim();

        storage.BeforeCheckpoint = sequences =>
        {
            sequences.ShouldContain((long)active.Sequence.Value);
            checkpointEntered.Set();
            releaseCheckpoint.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        var checkpoint = Task.Factory.StartNew(coordinator.Checkpoint,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<ITransactionContext>? lifecycle = null;

        try
        {
            checkpointEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            // For begin, the allocator signals that the operation reached the
            // coordinator while its journal gate is held by the checkpoint.
            storage.SequenceReserved = () => lifecycleStarted.Set();
            lifecycle = Task.Factory.StartNew(() =>
            {
                if (commit)
                {
                    lifecycleStarted.Set();
                    coordinator.CommitAsync(active).AsTask().GetAwaiter().GetResult();
                    return active;
                }

                return coordinator.BeginAsync(IsolationLevel.Snapshot).AsTask().GetAwaiter().GetResult();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            lifecycleStarted.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            // An append that escaped the gate could complete here, between the
            // captured active list and the actual WAL truncation.
            (await Task.WhenAny(lifecycle, Task.Delay(150))).ShouldNotBeSameAs(lifecycle);
        }
        finally
        {
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
            if (lifecycle is not null)
            {
                await lifecycle.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        var appended = await lifecycle!;
        var plan = TransactionRecovery.Analyze(storage.Log);
        if (commit)
        {
            // The commit must follow the checkpoint carrying its active begin;
            // truncating it instead would resurrect an aborted classification.
            plan.Committed.ShouldContain(active.Sequence);
            plan.Aborted.ShouldNotContain(active.Sequence);
        }
        else
        {
            // The old begin is represented by the checkpoint; the new begin
            // survives after it. Both still require crash-time undo.
            plan.Aborted.ShouldContain(active.Sequence);
            plan.Aborted.ShouldContain(appended.Sequence);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: recovery scrubs before truncation and anchors the idle prune bound")]
    public async Task Recovery_ReopenedJournal_ShouldScrubBeforeCheckpointAndPruneOldTombstones()
    {
        using var original = CoordinatorStorage.Create();
        var committed = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        var deleted = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        var aborted = new TransactionSequence((ulong)original.ReserveTransactionSequence());
        original.Log.AppendBegin((long)committed.Value);
        original.Log.AppendCommit((long)committed.Value);
        original.Log.AppendBegin((long)deleted.Value);
        original.Log.AppendCommit((long)deleted.Value);
        original.Log.AppendBegin((long)aborted.Value);

        (PageId PageId, int SlotIndex) retained;
        using (var statement = original.BeginTransaction())
        {
            original.Insert(statement, Stamped(committed, deleted));
            original.Insert(statement, Stamped(aborted, TransactionSequence.None));
            retained = original.Insert(statement, Stamped(committed, aborted));
            statement.Commit();
        }

        // Capture without clean disposal: all statement page changes survived,
        // while the logical aborted writer has no durable commit record.
        var images = original.CaptureImages();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var coordinator = new TransactionCoordinator(reopened, reopened.Log, reopened);
        coordinator.Manager.OldestActive.Value.ShouldBeLessThan(deleted.Value);

        var plan = coordinator.AnalyzeAndScrub();

        plan.Aborted.ShouldContain(aborted);
        RecordCount(reopened).ShouldBe(2);
        BinaryPrimitives.ReadUInt64LittleEndian(reopened.Read(retained.PageId, retained.SlotIndex).Span.Slice(8, 8))
            .ShouldBe(0UL);
        coordinator.VersionStore.TrackedVersionCount.ShouldBe(1);
        // The caller still needs these lifecycle records to scrub its indexes.
        TransactionRecovery.Analyze(reopened.Log).Aborted.ShouldContain(aborted);

        // No post-restart logical begin has advanced the fresh manager. The
        // recovered sequence floor alone makes this old tombstone reclaimable.
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(1);
        RecordCount(reopened).ShouldBe(1);

        coordinator.CompleteRecovery();

        reopened.Log.ReadAll().Select(record => record.Type).ShouldBe([JournalRecordType.Checkpoint]);
        TransactionRecovery.Analyze(reopened.Log).Aborted.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: an older snapshot minimum pins a committed deleter")]
    public async Task Prune_CommittedDeleterBelowOldestActive_ShouldKeepPinnedSnapshotVersion()
    {
        using var storage = CoordinatorStorage.Create();
        (PageId PageId, int SlotIndex) location;
        using (var setup = storage.BeginTransaction())
        {
            location = storage.Insert(setup, Stamped(TransactionSequence.None, TransactionSequence.None));
            setup.Commit();
        }

        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var deleter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var pinned = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        pinned.Snapshot.Minimum.ShouldBe(deleter.Sequence);

        await coordinator.ApplyStatementAsync(deleter, bracket =>
        {
            storage.Update(bracket, location.PageId, location.SlotIndex,
                Stamped(TransactionSequence.None, deleter.Sequence));
            coordinator.VersionStore.RecordTombstoned(deleter.Sequence, location.PageId, location.SlotIndex);
            return 0;
        });
        await coordinator.CommitAsync(deleter);

        coordinator.Manager.OldestActive.ShouldBe(pinned.Sequence);
        coordinator.Manager.OldestActive.Value.ShouldBeGreaterThan(deleter.Sequence.Value);
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0,
            storage.PackLocation(location.PageId, location.SlotIndex), pinned.Snapshot)).ShouldNotBeNull();

        await coordinator.CommitAsync(pinned);

        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(1);
        RecordCount(storage).ShouldBe(0);
    }

    private static byte[] Stamped(TransactionSequence writer, TransactionSequence deleter)
    {
        var bytes = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), writer.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), deleter.Value);
        bytes[16] = 42;
        return bytes;
    }

    private static int RecordCount(IStorage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    // Only the boundary hooks are test doubles. Pages, record iteration,
    // transactions, durability, journal replay, and truncation are real Storage.
    private sealed class CoordinatorStorage : Storage.Storage, IStorage, ITransactionRecordSpace
    {
        private readonly MemoryStream _data;
        private readonly MemoryStream _journal;

        private CoordinatorStorage(MemoryStream data, MemoryStream journal, bool reopen)
            : base(new StorageStream(new SimulatedDurableFileHandle(data)), new StorageStream(new SimulatedDurableFileHandle(journal)), new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;
            if (reopen)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)"coordinator-test");
            }
        }

        public override StorageModel Model => StorageModel.KeyValue;

        internal IStorageJournal Log => WriteAheadLog;

        internal Action<long[]>? BeforeCheckpoint { get; set; }

        internal Action? SequenceReserved { get; set; }

        internal static CoordinatorStorage Create() => new(new MemoryStream(), new MemoryStream(), reopen: false);

        internal static CoordinatorStorage Open(byte[] data, byte[] journal)
            => new(Copy(data), Copy(journal), reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureImages()
        {
            Flush();
            return (_data.ToArray(), _journal.ToArray());
        }

        internal (PageId PageId, int SlotIndex) Insert(IStorageTransaction bracket, ReadOnlySpan<byte> data)
            => InsertRecord(bracket, data);

        public ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        public void Update(IStorageTransaction bracket, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(bracket, pageId, slotIndex, record);

        public void Delete(IStorageTransaction bracket, PageId pageId, int slotIndex)
            => DeleteRecord(bracket, pageId, slotIndex);

        public ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        public (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

        void IStorage.Checkpoint(ReadOnlySpan<long> sequences)
        {
            BeforeCheckpoint?.Invoke(sequences.ToArray());
            Checkpoint(sequences);
        }

        long IStorage.ReserveTransactionSequence()
        {
            long sequence = ReserveTransactionSequence();
            SequenceReserved?.Invoke();
            return sequence;
        }

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }
}
