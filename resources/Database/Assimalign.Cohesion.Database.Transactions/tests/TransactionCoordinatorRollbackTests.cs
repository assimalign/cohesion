using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Fault-injection tests of the shared coordinator's rollback against real record pages, a real
/// write-ahead journal and the real lock manager (#1226): whatever fails or is canceled once a
/// rollback starts, the transaction ends, no lock outlives the undo of its versions, and the next
/// writer proceeds. Recovery classifies a writer whose abort record is missing as aborted.
/// </summary>
public class TransactionCoordinatorRollbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly LockResource Row = LockResource.Entry(7, 7);

    /// <summary>The journal writes of the undo's storage bracket a test fails.</summary>
    public enum UndoJournalWrite
    {
        /// <summary>The bracket's begin record.</summary>
        BracketBegin,

        /// <summary>The before image of the first page the undo changes.</summary>
        PageImage,
    }

    /// <summary>The awaits inside the record-space undo a caller's token used to reach.</summary>
    public enum UndoAwait
    {
        /// <summary>The wait for the statement apply gate another statement holds.</summary>
        ApplyGate,

        /// <summary>The undo of an index entry.</summary>
        IndexUndo,
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a journal that rejects the abort record releases the writer for the next one")]
    public async Task RollbackAsync_JournalRejectsAbortRecord_ShouldReleaseWriterForTheNextOne()
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        var journal = new FaultInjectingJournal(storage.Log);
        await using var coordinator = new TransactionCoordinator(storage, journal, storage);
        var writer = await BeginWriterAsync(coordinator, storage);
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        journal.FailRollbackRecords = true;

        // Act
        await coordinator.RollbackAsync(writer);

        // Assert: the abort record was lost, and the rollback still ended everything.
        journal.RejectedRollbacks.ShouldBe([(long)writer.Sequence.Value]);
        writer.State.ShouldBe(TransactionState.RolledBack);
        coordinator.GetOpenContexts().ShouldBe([next]);
        coordinator.Manager.OldestActive.ShouldBe(next.Sequence);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        RecordCount(storage).ShouldBe(0);
        await waiting.WaitAsync(Timeout);

        // The undo completed, so later checkpoints stop carrying the writer.
        Checkpoint(coordinator, storage).ShouldBe([(long)next.Sequence.Value]);

        // The next writer proceeds to a commit.
        journal.FailRollbackRecords = false;
        await InsertAsync(coordinator, storage, next);
        await coordinator.CommitAsync(next);
        RecordCount(storage).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a token canceled at an await of the undo does not stop it")]
    [InlineData(UndoAwait.ApplyGate)]
    [InlineData(UndoAwait.IndexUndo)]
    public async Task RollbackAsync_TokenCanceledDuringUndo_ShouldRunToCompletion(UndoAwait step)
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var index = new BlockingIndex();
        var writer = await BeginWriterAsync(coordinator, storage, step == UndoAwait.IndexUndo ? index : null);
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? heldStatement = null;
        if (step == UndoAwait.ApplyGate)
        {
            // Another statement holds the apply gate the undo must wait for.
            var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            heldStatement = coordinator.ApplyStatementAsync<int>(holder, async _ =>
            {
                gateHeld.TrySetResult();
                await releaseGate.Task.ConfigureAwait(false);
                return 0;
            }).AsTask();
            await gateHeld.Task.WaitAsync(Timeout);
        }

        using var canceled = new CancellationTokenSource();

        // Act: cancel while the undo waits at the step, then let the step finish.
        var rollback = coordinator.RollbackAsync(writer, canceled.Token).AsTask();
        try
        {
            if (step == UndoAwait.IndexUndo)
            {
                await index.Entered.WaitAsync(Timeout);
            }

            canceled.Cancel();
        }
        finally
        {
            releaseGate.TrySetResult();
            index.Release();
        }

        await rollback.WaitAsync(Timeout);
        if (heldStatement is not null)
        {
            await heldStatement.WaitAsync(Timeout);
        }

        // Assert
        writer.State.ShouldBe(TransactionState.RolledBack);
        index.Tokens.ShouldAllBe(token => !token.CanBeCanceled);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        coordinator.GetOpenContexts().ShouldNotContain(writer);
        RecordCount(storage).ShouldBe(0);
        await waiting.WaitAsync(Timeout);
        await InsertAsync(coordinator, storage, next);
        await coordinator.CommitAsync(next);
        await coordinator.CommitAsync(holder);
        RecordCount(storage).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a failed undo holds the writer's locks and visibility until the purge pass completes it")]
    public async Task RollbackAsync_UndoFails_ShouldHoldLocksAndVisibilityUntilThePurgePassCompletesIt()
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        var journal = new FaultInjectingJournal(storage.Log);
        await using var coordinator = new TransactionCoordinator(storage, journal, storage);
        var index = new FailingIndex(failures: 1);
        var writer = await BeginWriterAsync(coordinator, storage, index);
        var location = storage.PackLocation(storage.LastInserted.PageId, storage.LastInserted.SlotIndex);
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();

        // Act
        await coordinator.RollbackAsync(writer);

        // Assert: the caller's transaction is over...
        writer.State.ShouldBe(TransactionState.RolledBack);
        coordinator.GetOpenContexts().ShouldBe([next]);

        // ...but its version is still on the page, hidden from every snapshot because the writer
        // still counts as in flight; its lock is held; checkpoints keep classifying it; and its
        // abort record waits for the undo.
        RecordCount(storage).ShouldBe(1);
        var (pageId, slotIndex) = storage.UnpackLocation(location);
        RecordVersionStamp.ReadStamps(storage.Read(pageId, slotIndex).Span).Writer.ShouldBe(writer.Sequence);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBe([writer.Sequence.Value]);
        coordinator.Manager.OldestActive.ShouldBe(writer.Sequence);
        var probe = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        probe.Snapshot.IsVisible(writer.Sequence).ShouldBeFalse();
        (await coordinator.VersionStore.GetVisibleVersionAsync(0, location, probe.Snapshot)).ShouldBeNull();
        waiting.IsCompleted.ShouldBeFalse();
        Checkpoint(coordinator, storage).ShouldContain((long)writer.Sequence.Value);
        journal.AppendedRollbacks.ShouldNotContain((long)writer.Sequence.Value);

        // Act: the version-purge pass retries the undo.
        long undone = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert: undone, recorded, and released.
        undone.ShouldBe(2);
        RecordCount(storage).ShouldBe(0);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        journal.AppendedRollbacks.ShouldContain((long)writer.Sequence.Value);
        await waiting.WaitAsync(Timeout);
        coordinator.Manager.OldestActive.ShouldBe(next.Sequence);
        Checkpoint(coordinator, storage).ShouldNotContain((long)writer.Sequence.Value);
        coordinator.RunVersionPurgePass(CancellationToken.None).ShouldBe(0);
        await coordinator.CommitAsync(probe);
        await InsertAsync(coordinator, storage, next);
        await coordinator.CommitAsync(next);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: recovery classifies a writer without an abort record as aborted")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_WriterWithoutAbortRecord_ShouldBeClassifiedAbortedAndScrubbed(bool undoDeferred)
    {
        // Arrange: a rollback whose abort record never reached the journal — rejected after a
        // completed undo, or never attempted because the undo failed and a checkpoint truncated
        // the writer's begin record.
        using var storage = RollbackStorage.Create();
        var journal = new FaultInjectingJournal(storage.Log) { FailRollbackRecords = true };
        var coordinator = new TransactionCoordinator(storage, journal, storage);
        var writer = await BeginWriterAsync(coordinator, storage, undoDeferred ? new FailingIndex(failures: int.MaxValue) : null);
        await coordinator.RollbackAsync(writer);
        writer.State.ShouldBe(TransactionState.RolledBack);
        RecordCount(storage).ShouldBe(undoDeferred ? 1 : 0);
        if (undoDeferred)
        {
            coordinator.Checkpoint();
        }

        // Act: crash, reopen, and analyze the surviving journal.
        var images = storage.CaptureImages();
        using var reopened = RollbackStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened);
        var plan = recovered.AnalyzeAndScrub();

        // Assert: no commit record, so the writer is aborted and nothing it wrote survives.
        journal.AppendedRollbacks.ShouldBeEmpty();
        plan.Committed.ShouldNotContain(writer.Sequence);
        plan.Aborted.ShouldContain(writer.Sequence);
        RecordCount(reopened).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator lock manager: releasing a deferred writer's locks waits for its undo")]
    public async Task LockManager_ReleaseAllForWriterWithDeferredUndo_ShouldKeepItsLocksUntilTheUndoCompletes()
    {
        // Arrange: a rolled-back writer whose undo failed still holds the row lock.
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await BeginWriterAsync(coordinator, storage, new FailingIndex(failures: 1));
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        await coordinator.RollbackAsync(writer);
        writer.State.ShouldBe(TransactionState.RolledBack);

        // Act: an operation of the rolled-back transaction cleans up a grant it received after
        // the end, as the engines' writer-lock helpers do.
        await coordinator.LockManager.AcquireAsync(writer.Sequence, Row, LockMode.Exclusive);
        coordinator.LockManager.ReleaseAll(writer.Sequence);

        // Assert: the writer still holds the row, so the next writer cannot build on its
        // versions before the undo removes them.
        coordinator.LockManager.TryAcquire(next.Sequence, Row, LockMode.Exclusive).ShouldBeFalse();
        RecordCount(storage).ShouldBe(1);

        // Act: the purge pass completes the undo, and the manager releases the writer.
        coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        await waiting.WaitAsync(Timeout);
        RecordCount(storage).ShouldBe(0);

        // A transaction the manager no longer tracks releases through the same lock manager.
        var other = LockResource.Entry(7, 8);
        coordinator.LockManager.TryAcquire(writer.Sequence, other, LockMode.Exclusive).ShouldBeTrue();
        coordinator.LockManager.TryAcquire(next.Sequence, other, LockMode.Exclusive).ShouldBeFalse();
        coordinator.LockManager.ReleaseAll(writer.Sequence);
        coordinator.LockManager.TryAcquire(next.Sequence, other, LockMode.Exclusive).ShouldBeTrue();

        // An active transaction keeps its locks until it ends.
        var probe = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        coordinator.LockManager.ReleaseAll(next.Sequence);
        coordinator.LockManager.TryAcquire(probe.Sequence, Row, LockMode.Exclusive).ShouldBeFalse();
        await coordinator.CommitAsync(next);
        coordinator.LockManager.TryAcquire(probe.Sequence, Row, LockMode.Exclusive).ShouldBeTrue();
        await coordinator.CommitAsync(probe);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a journal that rejects the undo's own writes defers the undo and leaves checkpoints running")]
    [InlineData(UndoJournalWrite.BracketBegin)]
    [InlineData(UndoJournalWrite.PageImage)]
    public async Task RollbackAsync_UndoBracketJournalWriteFails_ShouldDeferTheUndoAndLeaveCheckpointsRunning(UndoJournalWrite failing)
    {
        // Arrange
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await BeginWriterAsync(coordinator, storage);
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();

        // Act: the undo's storage bracket cannot write its begin record (its first write), or the
        // before image of the page it undoes (its second).
        storage.JournalStream.SkipWrites = failing == UndoJournalWrite.PageImage ? 1 : 0;
        storage.JournalStream.FailWrites = 1;
        await coordinator.RollbackAsync(writer);

        // Assert: the transaction ended, its version and its lock wait for the undo, and the
        // failed bracket left nothing active in the storage, so checkpoints still run.
        storage.JournalStream.FailWrites.ShouldBe(0);
        writer.State.ShouldBe(TransactionState.RolledBack);
        RecordCount(storage).ShouldBe(1);
        coordinator.LockManager.TryAcquire(next.Sequence, Row, LockMode.Exclusive).ShouldBeFalse();
        Checkpoint(coordinator, storage).ShouldBe([(long)writer.Sequence.Value, (long)next.Sequence.Value], ignoreOrder: true);

        // Act: the purge pass retries the undo.
        coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        await waiting.WaitAsync(Timeout);
        RecordCount(storage).ShouldBe(0);
        Checkpoint(coordinator, storage).ShouldBe([(long)next.Sequence.Value]);
        await coordinator.CommitAsync(next);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator dispose: a writer whose undo still fails survives a clean close for recovery to scrub")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_UndoStillFailsAndStorageClosesCleanly_ShouldLeaveTheWriterForRecoveryToScrub(bool checkpointFirst)
    {
        // Arrange: a rolled-back writer whose undo fails at the rollback and at every retry.
        var storage = RollbackStorage.Create();
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await BeginWriterAsync(coordinator, storage, new FailingIndex(failures: int.MaxValue));
        await coordinator.RollbackAsync(writer);
        writer.State.ShouldBe(TransactionState.RolledBack);
        if (checkpointFirst)
        {
            // The checkpoint truncates the writer's begin record and carries it in its active list.
            Checkpoint(coordinator, storage).ShouldContain((long)writer.Sequence.Value);
        }

        // Act: the coordinator reports the undo it could not complete, and the owner closes the
        // storage cleanly anyway, as every engine does.
        await Should.ThrowAsync<IOException>(async () => await coordinator.DisposeAsync());
        storage.Dispose();
        var images = storage.CaptureClosedImages();
        using var reopened = RollbackStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened);
        var plan = recovered.AnalyzeAndScrub();

        // Assert: the close did not erase the writer's classification, so recovery scrubbed it.
        plan.Committed.ShouldNotContain(writer.Sequence);
        plan.Aborted.ShouldContain(writer.Sequence);
        RecordCount(reopened).ShouldBe(0);
    }

    /// <summary>
    /// The #1225 end gate and the #1226 deferred undo meet. A rollback that starts while a statement
    /// of the writer applies waits for it (#1225), so its undo covers that statement's bracket too;
    /// when the undo then fails, the writer stays tracked and keeps its lock (#1226), but its end
    /// claim still refuses its next statement, so no bracket lands between the failed undo and the
    /// purge pass's retry, which removes everything.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a deferred undo covers the statement the rollback waited for and keeps refusing the writer's statements")]
    public async Task RollbackAsync_UnderARunningStatementWithFailingUndo_ShouldWaitDeferAndRefuseLaterStatements()
    {
        // Arrange: a writer whose index undo fails once, and a second insert of it held inside the apply gate.
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var writer = await BeginWriterAsync(coordinator, storage, new FailingIndex(failures: 1));
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var running = Task.Factory.StartNew(() => coordinator.ApplyStatementAsync(writer, bracket =>
        {
            entered.Set();
            release.Wait(Timeout).ShouldBeTrue();
            byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, writer.Sequence);
            var (pageId, slotIndex) = storage.Insert(bracket, record);
            coordinator.VersionStore.RecordCreated(writer.Sequence, pageId, slotIndex);
            return 0;
        }).AsTask().GetAwaiter().GetResult(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        entered.Wait(Timeout).ShouldBeTrue();

        // Act
        var rollingBack = coordinator.RollbackAsync(writer).AsTask();
        bool waitedForStatement = !rollingBack.IsCompleted;
        release.Set();
        await running.WaitAsync(Timeout);
        await rollingBack.WaitAsync(Timeout);
        int recordsWhileDeferred = RecordCount(storage);
        var pendingWhileDeferred = coordinator.VersionStore.PendingAbortedPurges;
        var refused = await Should.ThrowAsync<TransactionAbortedException>(async () => await InsertAsync(coordinator, storage, writer));
        bool rowReleasedWhileDeferred = waiting.IsCompleted;
        coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        waitedForStatement.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.RolledBack);
        recordsWhileDeferred.ShouldBe(2);
        pendingWhileDeferred.ShouldBe([writer.Sequence.Value]);
        refused.Message.ShouldContain("was not applied", Case.Sensitive);
        rowReleasedWhileDeferred.ShouldBeFalse();
        RecordCount(storage).ShouldBe(0);
        coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        await waiting.WaitAsync(Timeout);
        await coordinator.CommitAsync(next);
    }

    /// <summary>
    /// The coordinator's lock-manager view (#1226) and the end's failing of queued requests (#1225)
    /// compose: when a rollback defers its undo, the writer's request still queued behind another
    /// transaction fails at once and is never granted, while the writer keeps the lock it holds,
    /// whatever an engine's clean-up asks of the view, until the purge pass completes the undo.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Coordinator rollback: a deferred undo fails the writer's queued lock request at once and keeps its grants")]
    public async Task RollbackAsync_UndoDeferredWhileWriterWaitsForALock_ShouldFailTheWaitAndKeepTheWritersLocks()
    {
        // Arrange: the writer holds Row and waits for another row a running transaction holds.
        using var storage = RollbackStorage.Create();
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage);
        var otherRow = LockResource.Entry(7, 9);
        var writer = await BeginWriterAsync(coordinator, storage, new FailingIndex(failures: 1));
        var blocker = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.LockManager.AcquireAsync(blocker.Sequence, otherRow, LockMode.Exclusive);
        var parked = coordinator.LockManager.AcquireAsync(writer.Sequence, otherRow, LockMode.Exclusive).AsTask();
        var next = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiting = coordinator.LockManager.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        bool parkedBeforeTheEnd = !parked.IsCompleted;

        // Act: the rollback ends the writer with its undo deferred; an engine then cleans up after
        // the writer through the view, which leaves the release to the manager.
        await coordinator.RollbackAsync(writer);
        var abandoned = await Should.ThrowAsync<TransactionAbortedException>(async () => await parked.WaitAsync(Timeout));
        coordinator.LockManager.ReleaseAll(writer.Sequence);
        bool rowReleasedBeforeTheUndo = waiting.IsCompleted;
        await coordinator.CommitAsync(blocker);
        bool otherRowFreeAfterTheBlocker = coordinator.LockManager.TryAcquire(next.Sequence, otherRow, LockMode.Exclusive);
        coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        parkedBeforeTheEnd.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.RolledBack);
        abandoned.Message.ShouldStartWith($"Transaction {writer.Sequence} ended while it waited", Case.Sensitive);
        rowReleasedBeforeTheUndo.ShouldBeFalse();
        otherRowFreeAfterTheBlocker.ShouldBeTrue();
        await waiting.WaitAsync(Timeout);
        RecordCount(storage).ShouldBe(0);
        await coordinator.CommitAsync(next);
    }

    private static long[] Checkpoint(TransactionCoordinator coordinator, RollbackStorage storage)
    {
        long[] captured = [];
        storage.BeforeCheckpoint = sequences => captured = sequences;
        coordinator.Checkpoint();
        storage.BeforeCheckpoint = null;
        return captured;
    }

    /// <summary>
    /// Begins a transaction that inserted one stamped record (and, given an index, tombstoned one
    /// index entry) and holds the row lock.
    /// </summary>
    private static async Task<ITransactionContext> BeginWriterAsync(
        TransactionCoordinator coordinator, RollbackStorage storage, IRecordVersionIndex? index = null)
    {
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, writer);
        if (index is not null)
        {
            var (pageId, slotIndex) = storage.LastInserted;
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, new byte[] { 1 }, storage.PackLocation(pageId, slotIndex));
        }

        await coordinator.LockManager.AcquireAsync(writer.Sequence, Row, LockMode.Exclusive);
        return writer;
    }

    private static ValueTask<int> InsertAsync(TransactionCoordinator coordinator, RollbackStorage storage, ITransactionContext context)
        => coordinator.ApplyStatementAsync(context, bracket =>
        {
            byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, context.Sequence);
            record[RecordVersionStamp.HeaderSize] = 42;
            var (pageId, slotIndex) = storage.Insert(bracket, record);
            coordinator.VersionStore.RecordCreated(context.Sequence, pageId, slotIndex);
            return 0;
        });

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

    /// <summary>An index whose entry undo blocks until released, observing the token it is given.</summary>
    private sealed class BlockingIndex : IRecordVersionIndex
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CancellationToken> _tokens = new();

        internal Task Entered => _entered.Task;

        internal IReadOnlyList<CancellationToken> Tokens
        {
            get
            {
                lock (_tokens)
                {
                    return [.. _tokens];
                }
            }
        }

        internal void Release() => _released.TrySetResult();

        public ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
            => default;

        public async ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
        {
            lock (_tokens)
            {
                _tokens.Add(cancellationToken);
            }

            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>An index whose entry undo fails a set number of times.</summary>
    private sealed class FailingIndex : IRecordVersionIndex
    {
        private int _failures;

        internal FailingIndex(int failures)
        {
            _failures = failures;
        }

        public ValueTask EraseAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
            => default;

        public ValueTask ClearDeleterAsync(IStorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken = default)
        {
            if (_failures > 0)
            {
                _failures--;
                throw new IOException("Injected index undo failure.");
            }

            return default;
        }
    }

    /// <summary>The storage's own journal, with rollback records that can be rejected.</summary>
    private sealed class FaultInjectingJournal : IStorageJournal
    {
        private readonly IStorageJournal _inner;
        private readonly List<long> _appended = new();
        private readonly List<long> _rejected = new();

        internal FaultInjectingJournal(IStorageJournal inner)
        {
            _inner = inner;
        }

        internal bool FailRollbackRecords { get; set; }

        internal IReadOnlyList<long> AppendedRollbacks
        {
            get
            {
                lock (_appended)
                {
                    return [.. _appended];
                }
            }
        }

        internal IReadOnlyList<long> RejectedRollbacks
        {
            get
            {
                lock (_appended)
                {
                    return [.. _rejected];
                }
            }
        }

        public long LastLsn => _inner.LastLsn;

        public long DurableLsn => _inner.DurableLsn;

        public long AppendBegin(long transactionSequence) => _inner.AppendBegin(transactionSequence);

        public long AppendPageImage(long transactionSequence, PageId pageId, JournalRecordType type, ReadOnlySpan<byte> image)
            => _inner.AppendPageImage(transactionSequence, pageId, type, image);

        public long AppendOperation(long transactionSequence, ReadOnlySpan<byte> payload) => _inner.AppendOperation(transactionSequence, payload);

        public long AppendCommit(long transactionSequence) => _inner.AppendCommit(transactionSequence);

        public long AppendRollback(long transactionSequence)
        {
            lock (_appended)
            {
                if (FailRollbackRecords)
                {
                    _rejected.Add(transactionSequence);
                    throw new IOException("Injected abort-record failure.");
                }

                _appended.Add(transactionSequence);
            }

            return _inner.AppendRollback(transactionSequence);
        }

        public long Checkpoint(ReadOnlySpan<long> activeTransactions) => _inner.Checkpoint(activeTransactions);

        public void EnsureDurable(long lsn) => _inner.EnsureDurable(lsn);

        public void Flush(bool forceDurable = false) => _inner.Flush(forceDurable);

        public IReadOnlyList<JournalRecord> ReadAll() => _inner.ReadAll();

        // The storage owns and disposes its journal.
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    // Only the boundary hooks are test doubles. Pages, record iteration, transactions,
    // durability, journal replay, and truncation are real Storage.
    private sealed class RollbackStorage : Storage.Storage, IStorage, ITransactionRecordSpace
    {
        private readonly MemoryStream _data;
        private readonly FaultingMemoryStream _journal;

        private RollbackStorage(MemoryStream data, FaultingMemoryStream journal, bool reopen)
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
                InitializeNew((Name)"rollback-test");
            }
        }

        public override StorageModel Model => StorageModel.KeyValue;

        internal IStorageJournal Log => WriteAheadLog;

        /// <summary>Gets the journal's backing stream, whose writes a test can fail.</summary>
        internal FaultingMemoryStream JournalStream => _journal;

        internal Action<long[]>? BeforeCheckpoint { get; set; }

        internal (PageId PageId, int SlotIndex) LastInserted { get; private set; }

        internal static RollbackStorage Create() => new(new MemoryStream(), new FaultingMemoryStream(), reopen: false);

        internal static RollbackStorage Open(byte[] data, byte[] journal) => new(Copy(data), Copy(journal), reopen: true);

        internal (byte[] Data, byte[] Journal) CaptureImages()
        {
            Flush();
            return (_data.ToArray(), _journal.ToArray());
        }

        /// <summary>Gets the bytes a closed storage left behind (a memory stream keeps them after disposal).</summary>
        internal (byte[] Data, byte[] Journal) CaptureClosedImages() => (_data.ToArray(), _journal.ToArray());

        internal (PageId PageId, int SlotIndex) Insert(IStorageTransaction bracket, ReadOnlySpan<byte> data)
            => LastInserted = InsertRecord(bracket, data);

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

        private static FaultingMemoryStream Copy(byte[] bytes)
        {
            var stream = new FaultingMemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }
    }
}
