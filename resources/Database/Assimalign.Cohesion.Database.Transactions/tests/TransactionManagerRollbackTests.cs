using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

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
        // Arrange
        var kernel = Kernel.Create();
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var next = await kernel.Manager.BeginAsync();
        var waiting = kernel.Locks.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();

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

        // Assert
        await waiting.WaitAsync(Timeout);
        kernel.Versions.PurgeCalls.ShouldBe(4);
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
        // Arrange
        var kernel = Kernel.Create();
        kernel.Log.FailCommit = true;
        kernel.Versions.FailPurges = 1;
        var writer = await kernel.BeginWriterAsync();
        var next = await kernel.Manager.BeginAsync();
        var waiting = kernel.Locks.AcquireAsync(next.Sequence, Row, LockMode.Exclusive).AsTask();

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

    private static byte[] Payload(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>A manager over controllable collaborators, with a writer helper.</summary>
    private sealed class Kernel
    {
        private Kernel(ControlledLog log, ControlledVersionStore versions, ILockManager locks, ITransactionManager manager)
        {
            Log = log;
            Versions = versions;
            Locks = locks;
            Manager = manager;
        }

        internal ControlledLog Log { get; }

        internal ControlledVersionStore Versions { get; }

        internal ILockManager Locks { get; }

        internal ITransactionManager Manager { get; }

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
    private sealed class ControlledLog : ITransactionLog
    {
        private readonly List<CancellationToken> _abortTokens = new();
        private int _abortAttempts;
        private int _abortRecords;
        private int _commitAttempts;

        internal bool FailAbort { get; set; }

        internal bool FailCommit { get; set; }

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

        public ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _commitAttempts);
            return FailCommit ? throw new IOException("Injected commit-record failure.") : default;
        }

        public async ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
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
