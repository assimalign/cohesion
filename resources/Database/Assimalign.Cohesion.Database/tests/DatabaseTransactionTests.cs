using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The explicit-transaction state machine of the root <see cref="DatabaseTransaction"/> base
/// (concrete-types plan §6.4, phase 3, #1259): the end gate, the Faulted state, the repeatable
/// rollback, cancellation observed only before an end starts, the abort and the session's teardown,
/// the offline refusal and every guard of the public members. Before the base, Graph, Documents,
/// Blob and KeyValuePair each carried a copy (#1188, #1225, #1226); the model suites gate each
/// model's adoption in phase 4.
/// </summary>
public class DatabaseTransactionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: the identity and isolation level are the constructor's")]
    public void Constructor_IdentityAndLevel_ShouldBeFixed()
    {
        // Arrange / Act
        var transaction = new TestTransaction(IsolationLevel.ReadCommitted);
        IDatabaseTransaction bridged = transaction;

        // Assert
        transaction.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        transaction.Id.Value.ShouldNotBe(Guid.Empty);
        bridged.Id.ShouldBe(transaction.Id);
        bridged.IsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        transaction.State.ShouldBe(TransactionState.Active);
        transaction.Open.ShouldBeTrue();
        transaction.Usable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit of an active transaction commits once and ends it")]
    public async Task CommitAsync_Active_ShouldCommitOnceAndEndTheTransaction()
    {
        // Arrange
        var transaction = new TestTransaction();

        // Act
        await transaction.CommitAsync();

        // Assert
        transaction.Commits.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Committed);
        transaction.Open.ShouldBeFalse();
        transaction.Usable.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a token canceled before the commit leaves the transaction as it was")]
    public async Task CommitAsync_CanceledBeforeItStarts_ShouldLeaveTheTransactionActive()
    {
        // Arrange
        var transaction = new TestTransaction();
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(source.Token));

        // Assert
        transaction.Commits.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Active);
        transaction.Usable.ShouldBeTrue();
        await transaction.CommitAsync();
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit that started runs to completion when its token is canceled")]
    public async Task CommitAsync_CanceledAfterItStarted_ShouldRunToCompletion()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { CommitBarrier = release.Task };
        using var source = new CancellationTokenSource();

        // Act
        var commit = transaction.CommitAsync(source.Token).AsTask();
        await transaction.CommitStarted.Task.WaitAsync(Timeout);
        source.Cancel();
        release.SetResult();
        await commit.WaitAsync(Timeout);

        // Assert
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a rollback that started runs to completion when its token is canceled")]
    public async Task RollbackAsync_CanceledAfterItStarted_ShouldRunToCompletion()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { RollbackBarrier = release.Task };
        using var source = new CancellationTokenSource();

        // Act
        var rollback = transaction.RollbackAsync(source.Token).AsTask();
        await transaction.RollbackStarted.Task.WaitAsync(Timeout);
        source.Cancel();
        release.SetResult();
        await rollback.WaitAsync(Timeout);

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a token canceled before the rollback leaves the transaction as it was")]
    public async Task RollbackAsync_CanceledBeforeItStarts_ShouldLeaveTheTransactionActive()
    {
        // Arrange
        var transaction = new TestTransaction();
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(source.Token));

        // Assert
        transaction.Rollbacks.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Active);
        transaction.Usable.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a token canceled while an end waits for the gate leaves the transaction as it was")]
    public async Task CommitAsync_CanceledWhileWaitingForTheEndGate_ShouldNotStart()
    {
        // Arrange: an abort holds the end gate in its rollback.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { RollbackBarrier = release.Task };
        var abort = transaction.Abort(new InvalidOperationException("statement failed")).AsTask();
        await transaction.RollbackStarted.Task.WaitAsync(Timeout);
        using var source = new CancellationTokenSource();

        // Act
        var commit = transaction.CommitAsync(source.Token).AsTask();
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await commit.WaitAsync(Timeout));
        release.SetResult();
        await abort.WaitAsync(Timeout);

        // Assert: the commit never started, so the aborted transaction still waits for its rollback.
        transaction.Commits.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Faulted);
        transaction.Open.ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a transaction that did not commit accepts any number of rollbacks")]
    public async Task RollbackAsync_Repeated_ShouldSucceedAndUndoOnce()
    {
        // Arrange
        var transaction = new TestTransaction();

        // Act
        await transaction.RollbackAsync();
        await transaction.RollbackAsync();
        await transaction.RollbackAsync();

        // Assert
        transaction.Rollbacks.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a committed transaction refuses a rollback")]
    public async Task RollbackAsync_Committed_ShouldThrow()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.CommitAsync();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.RollbackAsync());

        // Assert
        error.Message.ShouldBe("The transaction is Committed; a committed transaction cannot roll back.");
        transaction.Rollbacks.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a second commit of a committed transaction is refused with its state")]
    public async Task CommitAsync_AlreadyCommitted_ShouldThrowWithTheState()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.CommitAsync();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        error.Message.ShouldBe("The transaction is Committed.");
        transaction.Commits.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: an abort records the cause, rolls the kernel back and leaves the transaction Faulted and open")]
    public async Task Abort_StatementFailed_ShouldFaultTheTransactionUntilTheCallerEndsIt()
    {
        // Arrange
        var transaction = new TestTransaction();
        var cause = new InvalidOperationException("statement failed");

        // Act
        await transaction.Abort(cause);

        // Assert
        transaction.Rollbacks.ShouldBe(1);
        transaction.KernelState.ShouldBe(TransactionState.RolledBack);
        transaction.State.ShouldBe(TransactionState.Faulted);
        transaction.Open.ShouldBeTrue();
        transaction.Usable.ShouldBeFalse();
        transaction.BeginOperation().ShouldBeFalse();
        var refusal = transaction.Refusal();
        refusal.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted; operations are refused");
        refusal.Message.ShouldEndWith("Cause: statement failed");
        refusal.InnerException.ShouldBeSameAs(cause);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit after an abort commits nothing, fails with the coded error, and keeps failing so")]
    public async Task CommitAsync_AfterAbort_ShouldFailWithTheCodedErrorEveryTime()
    {
        // Arrange
        var transaction = new TestTransaction();
        var cause = new InvalidOperationException("statement failed");
        await transaction.Abort(cause);

        // Act
        var first = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        var second = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        transaction.Commits.ShouldBe(0);
        first.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted and cannot commit");
        first.InnerException.ShouldBeSameAs(cause);
        second.Message.ShouldBe(first.Message);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a rollback ends an aborted transaction without a second undo")]
    public async Task RollbackAsync_AfterAbort_ShouldEndTheTransaction()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.Abort(new InvalidOperationException("statement failed"));

        // Act
        await transaction.RollbackAsync();

        // Assert
        transaction.Rollbacks.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit queued behind an abort's rollback commits nothing and names the abort's cause")]
    public async Task CommitAsync_QueuedBehindAnAbort_ShouldCommitNothingAndNameTheCause()
    {
        // Arrange: an abort records its cause and holds the end gate in its rollback.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { RollbackBarrier = release.Task };
        var cause = new InvalidOperationException("statement failed");
        var abort = transaction.Abort(cause).AsTask();
        await transaction.RollbackStarted.Task.WaitAsync(Timeout);

        // Act
        var commit = transaction.CommitAsync().AsTask();
        release.SetResult();
        await abort.WaitAsync(Timeout);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await commit.WaitAsync(Timeout));

        // Assert: the commit saw the abort's outcome through the gate and undid nothing twice.
        error.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted and cannot commit");
        error.InnerException.ShouldBeSameAs(cause);
        transaction.Commits.ShouldBe(0);
        transaction.Rollbacks.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a transaction the kernel ended under its caller is Faulted until the caller ends it")]
    public async Task State_KernelEndedItUnderTheCaller_ShouldBeFaultedUntilTheCallerEndsIt()
    {
        // Arrange
        var transaction = new TestTransaction();
        transaction.KernelState = TransactionState.RolledBack;

        // Act
        var faulted = transaction.State;
        bool open = transaction.Open;
        var refusal = transaction.Refusal();
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        faulted.ShouldBe(TransactionState.Faulted);
        open.ShouldBeTrue();
        refusal.Message.ShouldStartWith(TestTransaction.AbortedCode + ": ");
        refusal.InnerException.ShouldBeNull();
        commit.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted and cannot commit");
        transaction.Commits.ShouldBe(0);
        transaction.Rollbacks.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: the end gate serializes a rollback and a commit, and the commit sees the rollback's outcome")]
    public async Task EndGate_CommitDuringRollback_ShouldWaitAndSeeTheRollback()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = new TestTransaction { RollbackBarrier = release.Task };

        // Act
        var rollback = transaction.RollbackAsync().AsTask();
        await transaction.RollbackStarted.Task.WaitAsync(Timeout);
        var commit = transaction.CommitAsync().AsTask();
        await Task.Delay(50);
        bool commitWaited = !commit.IsCompleted;
        var refusalWhileEnding = transaction.Refusal();
        release.SetResult();
        await rollback.WaitAsync(Timeout);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await commit.WaitAsync(Timeout));

        // Assert
        commitWaited.ShouldBeTrue();
        refusalWhileEnding.Message.ShouldBe("The session's transaction is being committed or rolled back; start the operation after it ends.");
        error.Message.ShouldBe("The transaction is RolledBack.");
        transaction.Commits.ShouldBe(0);
        transaction.Rollbacks.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit while an operation runs is refused and leaves the transaction active")]
    public async Task CommitAsync_OperationRunning_ShouldBeRefusedUntilItEnds()
    {
        // Arrange
        var transaction = new TestTransaction();
        transaction.BeginOperation().ShouldBeTrue();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        int running = transaction.Operations;
        var stateWhileRunning = transaction.State;
        transaction.FinishOperation();
        await transaction.CommitAsync();

        // Assert
        error.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        running.ShouldBe(1);
        stateWhileRunning.ShouldBe(TransactionState.Active);
        transaction.Commits.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: an operation is admitted only while the transaction is usable, and ends only once")]
    public async Task TryBeginOperation_EndedTransaction_ShouldRefuseAndEndOperationShouldBePaired()
    {
        // Arrange
        var transaction = new TestTransaction();

        // Act
        bool admitted = transaction.BeginOperation();
        transaction.FinishOperation();
        var unpaired = Should.Throw<InvalidOperationException>(() => transaction.FinishOperation());
        await transaction.RollbackAsync();
        bool afterRollback = transaction.BeginOperation();

        // Assert
        admitted.ShouldBeTrue();
        unpaired.Message.ShouldBe("No operation of the transaction is running.");
        afterRollback.ShouldBeFalse();
        transaction.Operations.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a refused operation after the caller's end names the end")]
    public async Task CreateRefusal_EndedByTheCaller_ShouldSayNothingWasWritten()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.RollbackAsync();

        // Act
        var refusal = transaction.Refusal();

        // Assert
        refusal.Message.ShouldBe("The session's transaction ended before the operation started; nothing was written.");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a commit the kernel aborts ends the transaction and surfaces the leaf's error")]
    public async Task CommitAsync_KernelAbortsTheCommit_ShouldEndTheTransactionWithTheLeafsError()
    {
        // Arrange
        var failure = new DatabaseTransactionAbortedException("write-write conflict");
        var transaction = new TestTransaction { CommitFailure = failure };

        // Act
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await transaction.CommitAsync());

        // Assert
        error.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
        await transaction.RollbackAsync();
        transaction.Rollbacks.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: an end the kernel refused before it started leaves nothing that can commit")]
    public async Task CommitAsync_AfterARefusedRollback_ShouldCommitNothing()
    {
        // Arrange: the database is closing, so the kernel refuses the rollback before it starts.
        var refusal = new ObjectDisposedException("coordinator");
        var transaction = new TestTransaction { RollbackRefusal = refusal };
        await Should.ThrowAsync<ObjectDisposedException>(async () => await transaction.RollbackAsync());
        transaction.RollbackRefusal = null;

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert: the commit completed the rollback the caller asked for and committed nothing.
        error.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted and cannot commit");
        transaction.Commits.ShouldBe(0);
        transaction.Rollbacks.ShouldBe(2);
        transaction.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: disposal rolls an active transaction back and releases the leaf once")]
    public async Task DisposeAsync_Active_ShouldRollBackAndReleaseOnce()
    {
        // Arrange
        var transaction = new TestTransaction();

        // Act
        await transaction.DisposeAsync();
        await transaction.DisposeAsync();

        // Assert
        transaction.Rollbacks.ShouldBe(1);
        transaction.DisposeCores.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Open.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: disposal of a committed transaction rolls nothing back")]
    public async Task DisposeAsync_Committed_ShouldNotRollBack()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.CommitAsync();

        // Act
        await transaction.DisposeAsync();

        // Assert
        transaction.Rollbacks.ShouldBe(0);
        transaction.DisposeCores.ShouldBe(1);
        transaction.State.ShouldBe(TransactionState.Committed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: the session's teardown rolls back and makes a later commit name the closure")]
    public async Task Close_OpenTransaction_ShouldRollBackAndReportTheClosureToALaterCommit()
    {
        // Arrange
        var transaction = new TestTransaction();
        var closure = new DatabaseException("The session closed before the transaction ended.");

        // Act
        await transaction.Close(closure);
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await transaction.RollbackAsync();

        // Assert
        transaction.Rollbacks.ShouldBe(1);
        transaction.Open.ShouldBeFalse();
        commit.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted and cannot commit");
        commit.Message.ShouldEndWith("Cause: The session closed before the transaction ended.");
        commit.InnerException.ShouldBeSameAs(closure);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: a transaction its caller ended keeps its own outcome through an abort or the teardown")]
    public async Task AbortAndClose_AfterTheCallerEndedIt_ShouldKeepTheOutcome()
    {
        // Arrange
        var transaction = new TestTransaction();
        await transaction.CommitAsync();

        // Act
        await transaction.Abort(new InvalidOperationException("late failure"));
        await transaction.Close(new DatabaseException("closed"));
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());

        // Assert
        transaction.State.ShouldBe(TransactionState.Committed);
        error.Message.ShouldBe("The transaction is Committed.");
        transaction.Rollbacks.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: an offline database refuses the commit and rollback before they start")]
    public async Task CommitAndRollback_Offline_ShouldThrowTheRefusalBeforeTheyStart()
    {
        // Arrange
        var offline = new DatabaseOfflineException("TESTX002", "TESTX002: The database is offline.", null);
        var transaction = new TestTransaction { OfflineRefusal = offline };

        // Act
        var commit = await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync());
        var rollback = await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync());

        // Assert
        commit.ShouldBeSameAs(offline);
        rollback.ShouldBeSameAs(offline);
        transaction.Commits.ShouldBe(0);
        transaction.Rollbacks.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.Active);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: on an offline database disposal, the teardown and an abort touch nothing")]
    public async Task DisposeCloseAbort_Offline_ShouldNotRollBack()
    {
        // Arrange
        var offline = new DatabaseOfflineException("TESTX002", "TESTX002: The database is offline.", null);
        var aborted = new TestTransaction { OfflineRefusal = offline };
        var closed = new TestTransaction { OfflineRefusal = offline };
        var disposed = new TestTransaction { OfflineRefusal = offline };

        // Act
        await aborted.Abort(new InvalidOperationException("failed"));
        await closed.Close(new DatabaseException("closed"));
        await disposed.DisposeAsync();

        // Assert
        aborted.Rollbacks.ShouldBe(0);
        closed.Rollbacks.ShouldBe(0);
        disposed.Rollbacks.ShouldBe(0);
        disposed.DisposeCores.ShouldBe(1);
        aborted.State.ShouldBe(TransactionState.Faulted);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Transaction: an abort and a teardown require a cause")]
    public async Task AbortAndClose_NullCause_ShouldThrow()
    {
        // Arrange
        var transaction = new TestTransaction();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await transaction.Abort(null!));
        await Should.ThrowAsync<ArgumentNullException>(async () => await transaction.Close(null!));
        transaction.State.ShouldBe(TransactionState.Active);
    }
}
