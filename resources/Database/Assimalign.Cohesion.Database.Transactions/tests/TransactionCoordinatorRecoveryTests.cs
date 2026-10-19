using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;

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
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var active = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // A writer: only a transaction that applied a statement is listed (#1242).
        await coordinator.ApplyStatementAsync(active, bracket =>
            storage.Insert(bracket, Stamped(active.Sequence, TransactionSequence.None)).SlotIndex);
        using var checkpointEntered = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var lifecycleStarted = new ManualResetEventSlim();

        coordinator.BeforeCheckpoint = sequences =>
        {
            sequences.ShouldContain((long)active.Sequence.Value);
            checkpointEntered.Set();
            releaseCheckpoint.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        };

        var checkpoint = Task.Factory.StartNew(coordinator.Checkpoint,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<TransactionContext>? lifecycle = null;

        try
        {
            checkpointEntered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            // For begin, the allocator signals that the operation reached the
            // coordinator while its journal gate is held by the checkpoint.
            coordinator.SequenceReserved = () => lifecycleStarted.Set();
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
        await using var coordinator = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
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

        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
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

    /// <summary>
    /// A rollback that runs while a statement of the transaction is inside the apply gate waits for
    /// that statement, undoes its bracket, and refuses every later one, so no record keeps the
    /// rolled-back sequence. Before the #1225 review the rollback took the empty ledger at once,
    /// the bracket landed afterwards, and once a checkpoint truncated the abort record, recovery
    /// read the tombstone as committed and the purge reclaimed a record that was never deleted.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: a rollback under a running statement undoes it and refuses later brackets")]
    public async Task RollbackAsync_WhileStatementApplies_ShouldUndoItAndSurviveCheckpointAndReopen()
    {
        // Arrange: a committed record, and a statement of the writer held inside the apply gate
        // before it tombstones the record.
        using var storage = CoordinatorStorage.Create();
        (PageId PageId, int SlotIndex) location;
        using (var setup = storage.BeginTransaction())
        {
            location = storage.Insert(setup, Stamped(TransactionSequence.None, TransactionSequence.None));
            setup.Commit();
        }

        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = Task.Factory.StartNew(() => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            storage.Update(bracket, location.PageId, location.SlotIndex, Stamped(TransactionSequence.None, writer.Sequence));
            coordinator.VersionStore.RecordTombstoned(writer.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        bool laterApplied = false;

        // Act
        var rollingBack = coordinator.RollbackAsync(writer).AsTask();
        bool waitedForStatement = !rollingBack.IsCompleted;
        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await rollingBack.WaitAsync(TimeSpan.FromSeconds(10));
        var refused = await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await coordinator.ApplyStatementAsync(writer, bracket =>
            {
                laterApplied = true;
                return 0;
            }));
        int pairedAfterEnd = coordinator.PairedTransactionCount;
        coordinator.Checkpoint();
        var images = storage.CaptureImages();
        await coordinator.DisposeAsync();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();
        long pruned = recovered.RunVersionPurgePass(CancellationToken.None);

        // Assert
        waitedForStatement.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.RolledBack);
        refused.Message.ShouldContain("was not applied", Case.Sensitive);
        laterApplied.ShouldBeFalse();
        pairedAfterEnd.ShouldBe(0);
        RecordVersionStamp.ReadStamps(storage.Read(location.PageId, location.SlotIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
        pruned.ShouldBe(0);
        RecordCount(reopened).ShouldBe(1);
        RecordVersionStamp.ReadStamps(reopened.Read(location.PageId, location.SlotIndex).Span).Deleter.ShouldBe(TransactionSequence.None);
    }

    /// <summary>
    /// A commit that starts while a statement of the transaction is inside the apply gate waits for
    /// it, so the commit record follows every bracket stamped with the sequence, and a statement
    /// after the commit is refused.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator: a commit waits for a running statement and refuses later brackets")]
    public async Task CommitAsync_WhileStatementApplies_ShouldWaitForItAndRefuseLaterBrackets()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        (PageId PageId, int SlotIndex) location = default;
        var running = Task.Factory.StartNew(() => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            location = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None));
            coordinator.VersionStore.RecordCreated(writer.Sequence, location.PageId, location.SlotIndex);
            return 0;
        }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();

        // Act
        var committing = coordinator.CommitAsync(writer).AsTask();
        bool waitedForStatement = !committing.IsCompleted;
        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        await committing.WaitAsync(TimeSpan.FromSeconds(10));
        await Should.ThrowAsync<TransactionAbortedException>(async () =>
            await coordinator.ApplyStatementAsync(writer, bracket => 0));
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);

        // Assert
        waitedForStatement.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.Committed);
        TransactionRecovery.Analyze(storage.Log).Committed.ShouldContain(writer.Sequence);
        (await coordinator.VersionStore.GetVisibleVersionAsync(0,
            storage.PackLocation(location.PageId, location.SlotIndex), reader.Snapshot)).ShouldNotBeNull();
        await coordinator.RollbackAsync(reader);
    }

    /// <summary>
    /// Only a transaction that applied a statement can have stamped a row version, so only
    /// writers are listed by a checkpoint and its anchor (#1242); readers, however many, cost
    /// the anchor nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: only writers are anchored, not readers")]
    public async Task Checkpoint_ReadersAndWriters_ShouldAnchorOnlyTheWriters()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var reader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var secondReader = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.ApplyStatementAsync(writer, bracket =>
            storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex);
        long[] listed = [];
        coordinator.BeforeCheckpoint = sequences => listed = sequences;

        // Act
        coordinator.Checkpoint();

        // Assert
        listed.ShouldBe([(long)writer.Sequence.Value]);
        storage.CheckpointActiveTransactions.ShouldBe([(long)writer.Sequence.Value]);
        var checkpointRecord = storage.Log.ReadAll().Single();
        checkpointRecord.Payload.Length.ShouldBe(sizeof(long));
        await coordinator.CommitAsync(writer);
        await coordinator.CommitAsync(reader);
        await coordinator.CommitAsync(secondReader);
    }

    /// <summary>
    /// A reader's begin record goes with a checkpoint's truncation, and the checkpoint does not
    /// list it. When it then applies a statement, the coordinator appends its begin record again
    /// before the statement's bracket, so a crash before its commit still finds it unproven and
    /// scrubs what it wrote. Without that, recovery would read its version as committed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a reader that writes after a checkpoint is announced again and scrubbed after a crash")]
    public async Task Checkpoint_ReaderWritesAfterTheCheckpoint_ShouldBeAnnouncedAgainAndScrubbedAfterACrash()
    {
        // Arrange: a reader across a checkpoint, which truncates its begin record.
        using var storage = CoordinatorStorage.Create();
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var late = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        coordinator.Checkpoint();
        bool namedAfterTheCheckpoint = storage.Log.ReadAll().Any(record => record.TransactionSequence == (long)late.Sequence.Value);

        // Act: it writes, then the process stops before it commits.
        await coordinator.ApplyStatementAsync(late, bracket =>
            storage.Insert(bracket, Stamped(late.Sequence, TransactionSequence.None)).SlotIndex);
        var announced = storage.Log.ReadAll()
            .Where(record => record.TransactionSequence == (long)late.Sequence.Value)
            .Select(record => record.Type)
            .ToArray();
        var images = storage.CaptureImages();
        using var reopened = CoordinatorStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        var plan = recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();

        // Assert: the uncommitted version is gone — the loss of the re-announcement would leave it
        // readable as committed — and the begin record is what named the transaction.
        plan.Aborted.ShouldContain(late.Sequence);
        RecordCount(reopened).ShouldBe(0);
        namedAfterTheCheckpoint.ShouldBeFalse();
        announced.ShouldBe([JournalRecordType.BeginTransaction]);
    }

    /// <summary>
    /// A statement holds the apply gate. The bounded checkpoint does not wait for it: it returns
    /// at once, and the statement runs the checkpoint as it ends, before it releases the gate, so
    /// a long statement in one database never parks the engine's checkpoint worker (#1254 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a held gate defers the checkpoint to the statement's end")]
    public async Task TryCheckpoint_GateHeldByAStatement_ShouldDeferTheCheckpointToTheStatementsEnd()
    {
        // Arrange: a statement that holds the gate until the test releases it.
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        int checkpoints = 0;
        coordinator.BeforeCheckpoint = _ => checkpoints++;
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = coordinator.ApplyStatementAsync<int>(writer, async bracket =>
        {
            int slot = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex;
            entered.SetResult();
            await release.Task;
            return slot;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Act
        bool ranWhileHeld = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);
        int checkpointsWhileHeld = checkpoints;
        release.SetResult();
        await statement.WaitAsync(TimeSpan.FromSeconds(10));
        int checkpointsAtTheStatementsEnd = checkpoints;
        bool ranWhenFree = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);

        // Assert: deferred, run once by the statement's end, and run directly once the gate is free.
        ranWhileHeld.ShouldBeFalse();
        checkpointsWhileHeld.ShouldBe(0);
        checkpointsAtTheStatementsEnd.ShouldBe(1);
        ranWhenFree.ShouldBeTrue();
        checkpoints.ShouldBe(2);
        storage.CheckpointActiveTransactions.ShouldBe([(long)writer.Sequence.Value]);
        await coordinator.CommitAsync(writer);
    }

    /// <summary>
    /// The checkpoint a statement ran for a deferred request fails. The statement does not fail
    /// for it: its own outcome was decided. The failure is thrown by the next bounded checkpoint,
    /// so the engine's checkpoint worker learns of it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a deferred checkpoint's failure reaches the next checkpoint call, not the statement")]
    public async Task TryCheckpoint_DeferredCheckpointFails_ShouldThrowFromTheNextCallNotTheStatement()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var failure = new InvalidOperationException("Injected checkpoint failure.");
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = coordinator.ApplyStatementAsync<int>(writer, async bracket =>
        {
            int slot = storage.Insert(bracket, Stamped(writer.Sequence, TransactionSequence.None)).SlotIndex;
            entered.SetResult();
            await release.Task;
            return slot;
        }, durable: false).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None).ShouldBeFalse();
        coordinator.BeforeCheckpoint = _ => throw failure;

        // Act
        release.SetResult();
        var statementError = await Record.ExceptionAsync(() => statement.WaitAsync(TimeSpan.FromSeconds(10)));
        coordinator.BeforeCheckpoint = null;
        var reported = Should.Throw<InvalidOperationException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None));
        bool ranAfterTheReport = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);

        // Assert
        statementError.ShouldBeNull();
        reported.ShouldBeSameAs(failure);
        ranAfterTheReport.ShouldBeTrue();
        await coordinator.CommitAsync(writer);
    }

    /// <summary>
    /// The apply gate is not reentrant. A checkpoint asked for from inside a statement apply would
    /// wait for the gate its own caller holds, forever; it is refused at once instead (#1254 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator checkpoint: a checkpoint inside a statement apply is refused, not deadlocked")]
    public async Task Checkpoint_InsideAStatementApply_ShouldBeRefusedNotDeadlocked()
    {
        // Arrange
        using var storage = CoordinatorStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        Exception? direct = null;
        Exception? bounded = null;

        // Act
        await coordinator.ApplyStatementAsync<int>(writer, bracket =>
        {
            direct = Record.Exception(() => coordinator.Checkpoint());
            bounded = Record.Exception(() => coordinator.TryCheckpoint(TimeSpan.FromSeconds(1), CancellationToken.None));
            return ValueTask.FromResult(0);
        }, durable: false).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        coordinator.Checkpoint();

        // Assert: both refused inside; the same call succeeds once the statement completed.
        direct.ShouldBeOfType<StorageTransactionException>();
        bounded.ShouldBeOfType<StorageTransactionException>();
        await coordinator.CommitAsync(writer);
    }

    private static byte[] Stamped(TransactionSequence writer, TransactionSequence deleter)
    {
        var bytes = new byte[17];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0, 8), writer.Value);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8, 8), deleter.Value);
        bytes[16] = 42;
        return bytes;
    }

    private static int RecordCount(Storage.Storage storage)
    {
        using var iterator = storage.GetUnitIterator();
        int count = 0;
        while (iterator.MoveNext())
        {
            count++;
        }

        return count;
    }

    // Only the record-space boundary is a test double. Pages, record iteration,
    // transactions, durability, journal replay, and truncation are real Storage; the
    // checkpoint and sequence-reservation hooks are the coordinator's own (#1257).
    private sealed class CoordinatorStorage : Storage.Storage
    {
        private readonly MemoryStream _data;
        private readonly MemoryStream _journal;

        private CoordinatorStorage(MemoryStream data, MemoryStream journal, bool reopen)
            : base(StorageModel.KeyValue, new StorageStream(new SimulatedDurableFileHandle(data)), new StorageStream(new SimulatedDurableFileHandle(journal)), new StorageStream(new MemoryStream()))
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

        internal StorageJournal Log => WriteAheadLog;

        internal static CoordinatorStorage Create() => new(new MemoryStream(), new MemoryStream(), reopen: false);

        internal static CoordinatorStorage Open(byte[] data, byte[] journal)
            => new(Copy(data), Copy(journal), reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureImages()
        {
            Flush();
            return (_data.ToArray(), _journal.ToArray());
        }

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> data)
            => InsertRecord(bracket, data);

        internal ReadOnlyMemory<byte> Read(PageId pageId, int slotIndex) => ReadRecord(pageId, slotIndex);

        internal void Update(StorageTransaction bracket, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
            => UpdateRecord(bracket, pageId, slotIndex, record);

        internal void Delete(StorageTransaction bracket, PageId pageId, int slotIndex)
            => DeleteRecord(bracket, pageId, slotIndex);

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
            private readonly CoordinatorStorage _storage;

            internal RecordSpace(CoordinatorStorage storage)
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

        private static MemoryStream Copy(byte[] bytes)
        {
            var stream = new MemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }
}
