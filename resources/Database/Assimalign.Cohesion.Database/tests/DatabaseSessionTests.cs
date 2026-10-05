using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The root <see cref="DatabaseSession"/> base (concrete-types plan §6.4, phase 3, #1259): the one
/// "already active" check with its one message, the session's ownership of its explicit
/// transaction, the operation hold, the teardown order, and every guard of the public members.
/// Before the base, five sessions carried the check with three messages.
/// </summary>
public class DatabaseSessionTests
{
    private const string AlreadyActive = "A transaction or operation is already active on this session.";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a BEGIN registers the transaction at the requested level, Snapshot by default")]
    public async Task BeginTransactionAsync_OpenSession_ShouldRegisterTheTransaction()
    {
        // Arrange
        var session = CreateSession();

        // Act
        var transaction = await session.BeginTransactionAsync();
        var level = session.LastIsolationLevel;

        // Assert
        level.ShouldBe(IsolationLevel.Snapshot);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.IsolationLevel.ShouldBe(IsolationLevel.Snapshot);
        await transaction.CommitAsync();
        session.CurrentTransaction.ShouldBeNull();
        var next = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        session.LastIsolationLevel.ShouldBe(IsolationLevel.ReadCommitted);
        session.CurrentTransaction.ShouldBeSameAs(next);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a BEGIN while a usable transaction is open is refused with the one message")]
    public async Task BeginTransactionAsync_TransactionOpen_ShouldThrowTheOneMessage()
    {
        // Arrange
        var session = CreateSession();
        var transaction = await session.BeginTransactionAsync();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());

        // Assert
        error.Message.ShouldBe(AlreadyActive);
        session.BeginCores.ShouldBe(1);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a BEGIN while the session's transaction is aborted gets the transaction's coded refusal")]
    public async Task BeginTransactionAsync_TransactionAborted_ShouldThrowTheTransactionsRefusal()
    {
        // Arrange
        var session = CreateSession();
        var transaction = (TestTransaction)await session.BeginTransactionAsync();
        var cause = new InvalidOperationException("statement failed");
        await transaction.Abort(cause);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());

        // Assert
        error.Message.ShouldStartWith(TestTransaction.AbortedCode + ": ");
        error.InnerException.ShouldBeSameAs(cause);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        transaction.State.ShouldBe(TransactionState.Faulted);
        await transaction.RollbackAsync();
        session.CurrentTransaction.ShouldBeNull();
        (await session.BeginTransactionAsync()).ShouldNotBeSameAs(transaction);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a BEGIN while an operation holds the session is refused with the one message")]
    public async Task BeginTransactionAsync_OperationHeld_ShouldThrowTheOneMessage()
    {
        // Arrange
        var session = CreateSession();
        session.EnterOperation().ShouldBeTrue();

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        bool second = session.EnterOperation();
        session.LeaveOperation();
        var transaction = await session.BeginTransactionAsync();

        // Assert
        error.Message.ShouldBe(AlreadyActive);
        second.ShouldBeFalse();
        session.CurrentTransaction.ShouldBeSameAs(transaction);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a BEGIN in flight refuses a second BEGIN and every operation")]
    public async Task BeginTransactionAsync_BeginInFlight_ShouldRefuseASecondBeginAndOperations()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession();
        session.BeginBarrier = release.Task;
        var first = session.BeginTransactionAsync().AsTask();
        await session.BeginStarted.Task.WaitAsync(Timeout);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        bool operation = session.EnterOperation();
        release.SetResult();
        var transaction = await first.WaitAsync(Timeout);

        // Assert
        error.Message.ShouldBe(AlreadyActive);
        operation.ShouldBeFalse();
        session.BeginCores.ShouldBe(1);
        session.CurrentTransaction.ShouldBeSameAs(transaction);
        session.EnterOperation().ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a canceled BEGIN never reaches the core and releases the session")]
    public async Task BeginTransactionAsync_Canceled_ShouldNotCallTheCore()
    {
        // Arrange
        var session = CreateSession();
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.BeginTransactionAsync(source.Token));

        // Assert
        session.BeginCores.ShouldBe(0);
        session.EnterOperation().ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a closed session refuses BEGIN, execution and an operation hold")]
    public async Task PublicMembers_ClosedSession_ShouldThrow()
    {
        // Arrange
        var session = CreateSession();
        await session.DisposeAsync();

        // Act
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync());
        var typed = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new TestRequest()));
        var text = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT 1"));
        var hold = Should.Throw<DatabaseException>(() => session.EnterOperation());

        // Assert
        begin.Message.ShouldBe("The session is closed.");
        typed.Message.ShouldBe("The session is closed.");
        text.Message.ShouldBe("The session is closed.");
        hold.Message.ShouldBe("The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
        session.BeginCores.ShouldBe(0);
        session.Executions.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: execution checks its arguments and the token before the core runs")]
    public async Task ExecuteAsync_InvalidArgumentsOrCanceled_ShouldNotCallTheCore()
    {
        // Arrange
        var session = CreateSession();
        using var source = new CancellationTokenSource();
        source.Cancel();

        // Act / Assert
        await Should.ThrowAsync<ArgumentNullException>(async () => await session.ExecuteAsync((Execution.QueryRequest)null!));
        await Should.ThrowAsync<ArgumentException>(async () => await session.ExecuteAsync(" "));
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(new TestRequest(), source.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT 1", null, source.Token));
        session.Executions.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: execution forwards the request, the text and the parameters to the core")]
    public async Task ExecuteAsync_Valid_ShouldForwardToTheCore()
    {
        // Arrange
        var session = CreateSession();
        var request = new TestRequest();
        var parameters = new Dictionary<string, object?> { ["id"] = 7 };

        // Act
        var typed = await session.ExecuteAsync(request);
        var text = await session.ExecuteAsync("SELECT @id", parameters);

        // Assert
        typed.ShouldBeSameAs(TestResult.Instance);
        text.ShouldBeSameAs(TestResult.Instance);
        session.LastRequest.ShouldBeSameAs(request);
        session.LastStatement.ShouldBe("SELECT @id");
        session.LastParameters.ShouldBeSameAs(parameters);
        session.Executions.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: an aborted transaction makes the session refuse work with its coded error")]
    public async Task ThrowIfTransactionRefuses_TransactionAborted_ShouldThrowTheRefusal()
    {
        // Arrange
        var session = CreateSession();
        session.ThrowIfRefused();
        var transaction = (TestTransaction)await session.BeginTransactionAsync();
        session.ThrowIfRefused();
        await transaction.Abort(new InvalidOperationException("statement failed"));

        // Act
        var error = Should.Throw<DatabaseException>(() => session.ThrowIfRefused());

        // Assert
        error.Message.ShouldStartWith(TestTransaction.AbortedCode + ": The session's transaction is aborted; operations are refused");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: disposal ends the leaf's operations, then rolls the transaction back as the teardown")]
    public async Task DisposeAsync_OpenTransaction_ShouldEndOperationsThenCloseTheTransaction()
    {
        // Arrange
        var session = CreateSession();
        var transaction = (TestTransaction)await session.BeginTransactionAsync();

        // Act
        await session.DisposeAsync();
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync());
        await session.DisposeAsync();

        // Assert: the leaf's teardown ran once, while the transaction was still active.
        session.Log.Entries.ShouldBe(new[] { "session:operations:Active" });
        session.State.ShouldBe(SessionState.Closed);
        session.CurrentTransaction.ShouldBeNull();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.Rollbacks.ShouldBe(1);
        commit.Message.ShouldEndWith("Cause: The session closed before the transaction ended.");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a session closed while a BEGIN runs ends that transaction and refuses the BEGIN")]
    public async Task DisposeAsync_DuringBegin_ShouldEndTheNewTransaction()
    {
        // Arrange
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession();
        session.BeginBarrier = release.Task;
        var begin = session.BeginTransactionAsync().AsTask();
        await session.BeginStarted.Task.WaitAsync(Timeout);

        // Act
        await session.DisposeAsync();
        release.SetResult();
        var error = await Should.ThrowAsync<DatabaseException>(async () => await begin.WaitAsync(Timeout));

        // Assert
        error.Message.ShouldBe("The session is closed.");
        var transaction = session.Begun.ShouldHaveSingleItem();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        transaction.DisposeCores.ShouldBe(1);
        session.CurrentTransaction.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: a failure of the leaf's teardown still closes the transaction and is rethrown as itself")]
    public async Task DisposeAsync_LeafTeardownFails_ShouldStillCloseTheTransaction()
    {
        // Arrange
        var failure = new InvalidOperationException("operation failed to close");
        var session = CreateSession();
        session.DisposeFailure = failure;
        var transaction = (TestTransaction)await session.BeginTransactionAsync();

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(async () => await session.DisposeAsync());

        // Assert
        error.ShouldBeSameAs(failure);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.State.ShouldBe(SessionState.Closed);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: two teardown failures are reported together")]
    public async Task DisposeAsync_TwoStepsFail_ShouldThrowBothInAnAggregate()
    {
        // Arrange
        var teardown = new InvalidOperationException("operation failed to close");
        var refusal = new ObjectDisposedException("coordinator");
        var session = CreateSession();
        session.DisposeFailure = teardown;
        var transaction = (TestTransaction)await session.BeginTransactionAsync();
        transaction.RollbackRefusal = refusal;

        // Act
        var error = await Should.ThrowAsync<AggregateException>(async () => await session.DisposeAsync());

        // Assert
        error.InnerExceptions.ShouldBe(new Exception[] { teardown, refusal });
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: the interface view is the same session, database and transaction")]
    public async Task InterfaceBridge_IDatabaseSession_ShouldExposeTheSameObjects()
    {
        // Arrange
        var session = CreateSession();
        IDatabaseSession bridged = session;

        // Act
        var transaction = await bridged.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        // Assert
        bridged.Database.ShouldBeSameAs(session.Database);
        transaction.ShouldBeSameAs(session.CurrentTransaction);
        bridged.CurrentTransaction.ShouldBeSameAs(transaction);
        bridged.State.ShouldBe(SessionState.Open);
        (await bridged.ExecuteAsync("SELECT 1")).ShouldBeSameAs(TestResult.Instance);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Session: the database is the constructor's and is required")]
    public void Constructor_Database_ShouldBeFixedAndRequired()
    {
        // Arrange
        var engine = new TestEngine();
        var database = new TestDatabase("appdb", engine);

        // Act
        var session = new TestSession(database);

        // Assert
        session.Database.ShouldBeSameAs(database);
        Should.Throw<ArgumentNullException>(() => new TestSession(null!));
    }

    private static TestSession CreateSession()
        => new(new TestDatabase("appdb", new TestEngine()));
}
