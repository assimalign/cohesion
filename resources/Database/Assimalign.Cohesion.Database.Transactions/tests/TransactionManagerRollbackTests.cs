using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Fault-injection tests for the rule that a rollback, once started, always ends its transaction
/// (#1226): a journal that rejects the abort record, a caller token canceled at each await, an undo
/// that fails, and a second attempt to end a transaction whose end is already running.
/// </summary>
public class TransactionManagerRollbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly LockResource Row = LockResource.Entry(1, 1);
    private static readonly LockResource OtherRow = LockResource.Entry(1, 2);

    // A lock owner the manager never assigned: no end of a managed transaction touches its requests.
    private static readonly TransactionSequence Outsider = new(ulong.MaxValue - 1);

    /// <summary>The steps of a rollback that await.</summary>
    public enum RollbackStep
    {
        /// <summary>The undo of the writer's versions.</summary>
        Undo,

        /// <summary>The abort record's append.</summary>
        AbortRecord,
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a journal that rejects the abort record still ends the transaction and releases its locks")]
    public async Task RollbackAsync_AbortRecordAppendFails_ShouldEndTransactionAndReleaseItsLocks()
    {
        // Arrange
        var kernel = Kernel.Create();
        await using var _ = kernel.Manager;
        kernel.Log.FailAbort = true;
        var writer = await kernel.BeginWriterAsync();
        var next = await kernel.Manager.BeginAsync();
        var waiting = kernel.Locks.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();

        // Act
        await kernel.Manager.RollbackAsync(writer);

        // Assert: the abort record was attempted and lost, and nothing else was.
        writer.State.ShouldBe(TransactionState.RolledBack);
        kernel.Log.AbortAttempts.ShouldBe(1);
        kernel.Log.AbortRecords.ShouldBe(0);
        kernel.Manager.OldestActive.ShouldBe(next.Sequence);
        await waiting.WaitAsync(Timeout);
        var probe = await kernel.Manager.BeginAsync();
        probe.Snapshot.IsVisible(writer.Sequence).ShouldBeTrue();
        (await kernel.Versions.GetVisibleVersionAsync(1, 1, probe.Snapshot)).ShouldBeNull();
        await kernel.Manager.CommitAsync(next);
        await kernel.Manager.CommitAsync(probe);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a token canceled while the rollback awaits does not stop it")]
    [InlineData(RollbackStep.Undo)]
    [InlineData(RollbackStep.AbortRecord)]
    public async Task RollbackAsync_TokenCanceledAtAnAwait_ShouldRunToCompletion(RollbackStep step)
    {
        // Arrange: the step blocks, honoring whatever token reaches it.
        var kernel = Kernel.Create();
        await using var _ = kernel.Manager;
        var block = new Blocker();
        if (step == RollbackStep.Undo)
        {
            kernel.Versions.Block = block;
        }
        else
        {
            kernel.Log.BlockAbort = block;
        }

        var writer = await kernel.BeginWriterAsync();
        var next = await kernel.Manager.BeginAsync();
        var waiting = kernel.Locks.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        using var canceled = new CancellationTokenSource();

        // Act: cancel while the rollback waits in the step, then let the step finish.
        var rollback = kernel.Manager.RollbackAsync(writer, canceled.Token).AsTask();
        try
        {
            await block.Entered.WaitAsync(Timeout);
            canceled.Cancel();
        }
        finally
        {
            block.Release();
        }

        await rollback.WaitAsync(Timeout);

        // Assert: the token reached neither step, and the rollback ended everything it started.
        writer.State.ShouldBe(TransactionState.RolledBack);
        kernel.Versions.PurgeTokens.ShouldAllBe(token => !token.CanBeCanceled);
        kernel.Log.AbortTokens.ShouldAllBe(token => !token.CanBeCanceled);
        kernel.Log.AbortRecords.ShouldBe(1);
        kernel.Manager.OldestActive.ShouldBe(next.Sequence);
        await waiting.WaitAsync(Timeout);
        await kernel.Manager.CommitAsync(next);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a token canceled before the rollback starts leaves the transaction as it was")]
    public async Task RollbackAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        var kernel = Kernel.Create();
        await using var _ = kernel.Manager;
        var writer = await kernel.BeginWriterAsync();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await kernel.Manager.RollbackAsync(writer, canceled.Token));

        // Assert: nothing ran, so the transaction still commits its write.
        writer.State.ShouldBe(TransactionState.Active);
        kernel.Versions.PurgeCalls.ShouldBe(0);
        kernel.Log.AbortAttempts.ShouldBe(0);
        kernel.Manager.OldestActive.ShouldBe(writer.Sequence);
        await kernel.Manager.CommitAsync(writer);
        var probe = await kernel.Manager.BeginAsync();
        (await kernel.Versions.GetVisibleVersionAsync(1, 1, probe.Snapshot)).ShouldNotBeNull();
        await kernel.Manager.CommitAsync(probe);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a failed undo ends the transaction but keeps its versions hidden and its locks held until the undo completes")]
    public async Task RollbackAsync_UndoFails_ShouldEndTransactionAndHoldVisibilityAndLocksForTheRetry()
    {
        // Arrange: the row's next owner is outside the manager, because disposal aborts every
        // transaction the manager still runs, and that abort fails the aborted transaction's own
        // queued requests (#1225); the outsider's wait ends only when the writer's locks release.
        var kernel = Kernel.Create();
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var waiting = kernel.Locks.AcquireAsync(Outsider, Row, LockMode.Exclusive).AsTask();

        // Act
        await kernel.Manager.RollbackAsync(writer);

        // Assert: the caller's transaction is over, but the abort record waits for the undo, the
        // writer still counts as in flight for every snapshot, and its locks are still held.
        writer.State.ShouldBe(TransactionState.RolledBack);
        kernel.Log.AbortAttempts.ShouldBe(0);
        waiting.IsCompleted.ShouldBeFalse();
        kernel.Manager.OldestActive.ShouldBe(writer.Sequence);
        var probe = await kernel.Manager.BeginAsync();
        probe.Snapshot.IsVisible(writer.Sequence).ShouldBeFalse();
        (await kernel.Versions.GetVisibleVersionAsync(1, 1, probe.Snapshot)).ShouldBeNull();
        await Should.ThrowAsync<TransactionAbortedException>(async () => await kernel.Manager.RollbackAsync(writer));

        // Act: disposal retries the undo before the database closes.
        await kernel.Manager.DisposeAsync();

        // Assert: three undos ran, the failed one, the probe's abort at disposal, and the retry.
        await waiting.WaitAsync(Timeout);
        kernel.Versions.PurgeCalls.ShouldBe(3);
        kernel.Log.AbortRecords.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Dispose: an undo that still fails at disposal is reported")]
    public async Task DisposeAsync_DeferredUndoStillFails_ShouldThrowTheUndoFailure()
    {
        // Arrange
        var kernel = Kernel.Create();
        kernel.Versions.FailPurges = 2;
        var writer = await kernel.BeginWriterAsync();
        await kernel.Manager.RollbackAsync(writer);

        // Act
        var error = await Should.ThrowAsync<IOException>(async () => await kernel.Manager.DisposeAsync());

        // Assert: the retry ran, and the abort record still waits for an undo that never completed.
        error.Message.ShouldBe(ControlledVersionStore.FailureMessage);
        kernel.Versions.PurgeCalls.ShouldBe(2);
        kernel.Log.AbortAttempts.ShouldBe(0);
        writer.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Commit: a commit record and an undo that both fail fault the transaction and defer the undo")]
    public async Task CommitAsync_CommitRecordAndUndoFail_ShouldFaultAndDeferTheUndo()
    {
        // Arrange: the row's next owner is outside the manager, so disposal's abort of the
        // transactions it still runs cannot end the wait the test observes.
        var kernel = Kernel.Create();
        kernel.Log.FailCommit = true;
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var waiting = kernel.Locks.AcquireAsync(Outsider, Row, LockMode.Exclusive).AsTask();

        // Act
        await Should.ThrowAsync<TransactionAbortedException>(async () => await kernel.Manager.CommitAsync(writer));

        // Assert: aborted and ended, with its versions still hidden and its locks held.
        writer.State.ShouldBe(TransactionState.Faulted);
        waiting.IsCompleted.ShouldBeFalse();
        kernel.Manager.OldestActive.ShouldBe(writer.Sequence);

        // Act: disposal completes the undo.
        await kernel.Manager.DisposeAsync();

        // Assert
        await waiting.WaitAsync(Timeout);
        kernel.Log.AbortRecords.ShouldBeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// The #1225 and #1226 lock rules meet at a deferred undo. The writer's transaction has ended,
    /// so its queued request fails at once, as the release at any other end fails it (#1225), and
    /// is never granted; its granted lock stays until the undo completes (#1226). At disposal, the
    /// abort of a transaction still running fails that transaction's queued request too, and the
    /// retried undo then releases the writer to the owner waiting behind it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a deferred undo fails the writer's queued lock request at once and keeps its granted lock")]
    public async Task RollbackAsync_UndoFailsWhileWriterWaitsForALock_ShouldFailTheWaitAndKeepTheGrant()
    {
        // Arrange: the writer holds Row and waits for OtherRow, which a running transaction holds;
        // a running transaction and an outsider wait for Row.
        var kernel = Kernel.Create();
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var blocker = await kernel.Manager.BeginAsync();
        await kernel.Locks.AcquireAsync(blocker.Sequence, OtherRow, LockMode.Exclusive);
        var parked = kernel.Locks.AcquireAsync(writer.Sequence, OtherRow, LockMode.Exclusive).AsTask();
        var next = await kernel.Manager.BeginAsync();
        var nextWaiting = kernel.Locks.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();
        var outsiderWaiting = kernel.Locks.AcquireAsync(Outsider, Row, LockMode.Exclusive).AsTask();
        bool parkedBeforeTheEnd = !parked.IsCompleted;

        // Act: the rollback ends the writer and defers its undo.
        await kernel.Manager.RollbackAsync(writer);
        var abandoned = await Should.ThrowAsync<TransactionAbortedException>(async () => await parked.WaitAsync(Timeout));
        bool rowReleasedBeforeTheUndo = nextWaiting.IsCompleted || outsiderWaiting.IsCompleted;
        await kernel.Manager.CommitAsync(blocker);
        bool otherRowFreeAfterTheBlocker = kernel.Locks.TryAcquire(Outsider, OtherRow, LockMode.Exclusive);

        // Act: disposal aborts the running transaction, then completes the writer's undo.
        await kernel.Manager.DisposeAsync();

        // Assert
        parkedBeforeTheEnd.ShouldBeTrue();
        writer.State.ShouldBe(TransactionState.RolledBack);
        abandoned.Message.ShouldStartWith($"Transaction {writer.Sequence} ended while it waited", Case.Sensitive);
        rowReleasedBeforeTheUndo.ShouldBeFalse();
        otherRowFreeAfterTheBlocker.ShouldBeTrue();
        var aborted = await Should.ThrowAsync<TransactionAbortedException>(async () => await nextWaiting.WaitAsync(Timeout));
        aborted.Message.ShouldStartWith($"Transaction {next.Sequence} ended while it waited", Case.Sensitive);
        next.State.ShouldBe(TransactionState.Faulted);
        await outsiderWaiting.WaitAsync(Timeout);
        kernel.Versions.PurgeCalls.ShouldBe(3);
    }

    /// <summary>
    /// A rolled-back writer whose undo is deferred has ended, so a lock request it makes afterwards
    /// (a late operation of the ended transaction) is refused at once instead of queuing: queued, it
    /// would join the wait-for graph, and a live transaction asking for a lock the writer still
    /// holds would be chosen as the deadlock victim of a transaction that no longer runs.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a writer whose undo is deferred gets no new lock, so it cannot deadlock a live transaction")]
    public async Task RollbackAsync_UndoDeferred_ShouldRefuseTheWritersLaterLockRequests()
    {
        // Arrange: the writer holds Row, a live transaction holds OtherRow, and the undo fails once.
        var kernel = Kernel.Create();
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var live = await kernel.Manager.BeginAsync();
        await kernel.Locks.AcquireAsync(live.Sequence, OtherRow, LockMode.Exclusive);
        await kernel.Manager.RollbackAsync(writer);
        writer.State.ShouldBe(TransactionState.RolledBack);

        // Act: a late request of the ended writer for the live transaction's row, then the live
        // transaction's request for the row the writer still holds.
        var late = kernel.Locks.AcquireAsync(writer.Sequence, OtherRow, LockMode.Exclusive).AsTask();
        bool lateFailedAtOnce = late.IsFaulted;
        bool tryAcquired = kernel.Locks.TryAcquire(writer.Sequence, LockResource.Entry(1, 3), LockMode.Shared);
        var regrant = kernel.Locks.AcquireAsync(writer.Sequence, Row, LockMode.Shared).AsTask();
        var liveWaiting = kernel.Locks.AcquireAsync(live.Sequence, Row, LockMode.Exclusive).AsTask();
        await Task.WhenAny(liveWaiting, Task.Delay(TimeSpan.FromMilliseconds(100)));
        bool liveStillWaiting = !liveWaiting.IsCompleted;

        // Act: disposal aborts the live transaction and completes the writer's undo.
        await kernel.Manager.DisposeAsync();

        // Assert: the late request failed without queuing, nothing new was granted, a re-grant of
        // the lock the writer holds still succeeds, and the live transaction waited for the undo
        // instead of being chosen as a deadlock victim; disposal's abort then ended its wait.
        lateFailedAtOnce.ShouldBeTrue();
        var refused = await Should.ThrowAsync<TransactionAbortedException>(async () => await late);
        refused.Message.ShouldStartWith($"Transaction {writer.Sequence} has ended", Case.Sensitive);
        tryAcquired.ShouldBeFalse();
        regrant.IsCompletedSuccessfully.ShouldBeTrue();
        liveStillWaiting.ShouldBeTrue();
        var ended = await Should.ThrowAsync<TransactionAbortedException>(async () => await liveWaiting.WaitAsync(Timeout));
        ended.ShouldNotBeOfType<TransactionDeadlockException>();
        kernel.Locks.TryAcquire(Outsider, Row, LockMode.Exclusive).ShouldBeTrue();
        kernel.Locks.TryAcquire(writer.Sequence, OtherRow, LockMode.Exclusive).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Commit: a commit record written but not made durable ends the transaction as committed")]
    public async Task CommitAsync_CommitRecordWrittenButNotDurable_ShouldEndCommittedAndReportItUnconfirmed()
    {
        // Arrange: the log appends the commit record, then fails its flush.
        var kernel = Kernel.Create();
        await using var _ = kernel.Manager;
        kernel.Log.UnconfirmCommit = true;
        kernel.Versions.FailPurges = int.MaxValue;
        var writer = await kernel.BeginWriterAsync();
        var waiting = kernel.Locks.AcquireAsync(Outsider, Row, LockMode.Exclusive).AsTask();

        // Act
        var error = await Should.ThrowAsync<TransactionCommitUnconfirmedException>(async () => await kernel.Manager.CommitAsync(writer));

        // Assert: committed, visible and released; nothing tried to undo it, and no abort record
        // contradicts the commit record.
        error.InnerException.ShouldBeOfType<IOException>();
        writer.State.ShouldBe(TransactionState.Committed);
        await waiting.WaitAsync(Timeout);
        kernel.Versions.PurgeCalls.ShouldBe(0);
        kernel.Log.AbortAttempts.ShouldBe(0);
        var reader = await kernel.Manager.BeginAsync();
        reader.Snapshot.IsVisible(writer.Sequence).ShouldBeTrue();
        (await kernel.Versions.GetVisibleVersionAsync(1, 1, reader.Snapshot)).ShouldNotBeNull();
        await Should.ThrowAsync<TransactionAbortedException>(async () => await kernel.Manager.RollbackAsync(writer));
        kernel.Log.UnconfirmCommit = false;
        await kernel.Manager.CommitAsync(reader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Rollback: a second end of a transaction whose rollback is running is refused")]
    public async Task RollbackAsync_WhileItsRollbackIsRunning_ShouldRefuseAnotherEnd()
    {
        // Arrange
        var kernel = Kernel.Create();
        await using var _ = kernel.Manager;
        var block = new Blocker();
        kernel.Versions.Block = block;
        var writer = await kernel.BeginWriterAsync();
        var rollback = kernel.Manager.RollbackAsync(writer).AsTask();
        TransactionAbortedException secondRollback;
        TransactionAbortedException commit;

        // Act
        try
        {
            await block.Entered.WaitAsync(Timeout);
            secondRollback = await Should.ThrowAsync<TransactionAbortedException>(
                async () => await kernel.Manager.RollbackAsync(writer).AsTask().WaitAsync(Timeout));
            commit = await Should.ThrowAsync<TransactionAbortedException>(
                async () => await kernel.Manager.CommitAsync(writer).AsTask().WaitAsync(Timeout));
        }
        finally
        {
            block.Release();
        }

        await rollback.WaitAsync(Timeout);

        // Assert: the running rollback is the only end that happened.
        secondRollback.Message.ShouldContain("already ending");
        commit.Message.ShouldContain("already ending");
        writer.State.ShouldBe(TransactionState.RolledBack);
        kernel.Log.CommitAttempts.ShouldBe(0);
        kernel.Versions.PurgeCalls.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Dispose: disposal waits for a rollback that is already running")]
    public async Task DisposeAsync_WhileARollbackIsRunning_ShouldWaitForTheRollbackToEnd()
    {
        // Arrange: a rollback blocked in its undo.
        var kernel = Kernel.Create();
        var block = new Blocker();
        kernel.Versions.Block = block;
        var writer = await kernel.BeginWriterAsync();
        var rollback = kernel.Manager.RollbackAsync(writer).AsTask();
        await block.Entered.WaitAsync(Timeout);
        Task dispose;
        bool disposedWhileRollbackRan;

        // Act
        try
        {
            dispose = kernel.Manager.DisposeAsync().AsTask();
            await Should.ThrowAsync<ObjectDisposedException>(async () => await kernel.Manager.BeginAsync());
            await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(250)));
            disposedWhileRollbackRan = dispose.IsCompleted;
        }
        finally
        {
            block.Release();
        }

        await rollback.WaitAsync(Timeout);
        await dispose.WaitAsync(Timeout);

        // Assert: disposal neither returned under the running rollback nor aborted its writer a
        // second time; the rollback ended the writer and appended its abort record.
        disposedWhileRollbackRan.ShouldBeFalse();
        writer.State.ShouldBe(TransactionState.RolledBack);
        kernel.Versions.PurgeCalls.ShouldBe(1);
        kernel.Log.AbortRecords.ShouldBe(1);
    }

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A manager over controllable collaborators, with a writer helper.</summary>
    private sealed class Kernel
    {
        private Kernel(ControlledLog log, ControlledVersionStore versions, ILockManager locks, TransactionManager manager)
        {
            Log = log;
            Versions = versions;
            Locks = locks;
            Manager = manager;
        }

        internal ControlledLog Log { get; }

        internal ControlledVersionStore Versions { get; }

        internal ILockManager Locks { get; }

        internal TransactionManager Manager { get; }

        internal static Kernel Create()
        {
            var log = new ControlledLog();
            var versions = new ControlledVersionStore();
            var locks = LockManager.Create();
            return new Kernel(log, versions, locks, TransactionManager.Create(log, locks, versions));
        }

        /// <summary>Begins a transaction that wrote one version and holds the row lock.</summary>
        internal async Task<ITransactionContext> BeginWriterAsync()
        {
            var writer = await Manager.BeginAsync();
            await Versions.AppendVersionAsync(1, 1, Payload("uncommitted"), writer.Sequence);
            await Locks.AcquireAsync(writer.Sequence, Row, LockMode.Exclusive);
            return writer;
        }
    }

    /// <summary>Holds a step open until released, signalling when the step was entered.</summary>
    private sealed class Blocker
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        internal void Release() => _released.TrySetResult();

        /// <summary>Waits for the release, observing the token the step was given.</summary>
        internal async Task WaitAsync(CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The in-memory version store with an injectable undo failure and gate.</summary>
    private sealed class ControlledVersionStore : IVersionStore
    {
        internal const string FailureMessage = "Injected undo failure.";
        private readonly IVersionStore _inner = VersionStore.CreateInMemory();
        private readonly List<CancellationToken> _purgeTokens = new();
        private int _failPurges;
        private int _purgeCalls;

        internal int FailPurges
        {
            get => Volatile.Read(ref _failPurges);
            set => Volatile.Write(ref _failPurges, value);
        }

        internal Blocker? Block { get; set; }

        internal int PurgeCalls => Volatile.Read(ref _purgeCalls);

        internal IReadOnlyList<CancellationToken> PurgeTokens
        {
            get
            {
                lock (_purgeTokens)
                {
                    return [.. _purgeTokens];
                }
            }
        }

        public ValueTask AppendVersionAsync(ulong objectId, ulong entryId, ReadOnlyMemory<byte> payload, TransactionSequence writer, CancellationToken cancellationToken = default)
            => _inner.AppendVersionAsync(objectId, entryId, payload, writer, cancellationToken);

        public ValueTask<ReadOnlyMemory<byte>?> GetVisibleVersionAsync(ulong objectId, ulong entryId, TransactionSnapshot snapshot, CancellationToken cancellationToken = default)
            => _inner.GetVisibleVersionAsync(objectId, entryId, snapshot, cancellationToken);

        public ValueTask<long> PruneAsync(TransactionSequence oldestActive, CancellationToken cancellationToken = default)
            => _inner.PruneAsync(oldestActive, cancellationToken);

        public async ValueTask<long> PurgeWriterAsync(TransactionSequence writer, CancellationToken cancellationToken = default)
        {
            lock (_purgeTokens)
            {
                _purgeTokens.Add(cancellationToken);
            }

            Interlocked.Increment(ref _purgeCalls);
            if (Block is { } block)
            {
                await block.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Interlocked.Decrement(ref _failPurges) >= 0)
            {
                throw new IOException(FailureMessage);
            }

            Interlocked.Exchange(ref _failPurges, 0);
            return await _inner.PurgeWriterAsync(writer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>An in-memory transaction log with injectable commit and abort-record failures and an abort gate.</summary>
    private sealed class ControlledLog : TransactionLog
    {
        private readonly List<CancellationToken> _abortTokens = new();
        private int _abortAttempts;
        private int _abortRecords;
        private int _commitAttempts;

        internal bool FailAbort { get; set; }

        internal bool FailCommit { get; set; }

        /// <summary>Appends the commit record, then fails its durable flush.</summary>
        internal bool UnconfirmCommit { get; set; }

        internal Blocker? BlockAbort { get; set; }

        internal int AbortAttempts => Volatile.Read(ref _abortAttempts);

        internal int AbortRecords => Volatile.Read(ref _abortRecords);

        internal int CommitAttempts => Volatile.Read(ref _commitAttempts);

        internal IReadOnlyList<CancellationToken> AbortTokens
        {
            get
            {
                lock (_abortTokens)
                {
                    return [.. _abortTokens];
                }
            }
        }

        public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public override ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _commitAttempts);
            if (UnconfirmCommit)
            {
                throw new TransactionCommitUnconfirmedException(
                    $"Transaction {sequence} committed, but its commit record could not be made durable.",
                    new IOException("Injected flush failure."));
            }

            return FailCommit ? throw new IOException("Injected commit-record failure.") : default;
        }

        public override async ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            lock (_abortTokens)
            {
                _abortTokens.Add(cancellationToken);
            }

            Interlocked.Increment(ref _abortAttempts);
            if (BlockAbort is { } block)
            {
                await block.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (FailAbort)
            {
                throw new IOException("Injected abort-record failure.");
            }

            Interlocked.Increment(ref _abortRecords);
        }
    }
}
