using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The #1225 audit of the key-value engine. A command is statement-atomic: it writes in one
/// physical bracket that a failure rolls back, so a failed command writes nothing and leaves the
/// explicit transaction active, as a failed SQL statement does; later commands stay inside the
/// transaction and ROLLBACK undoes them. The transaction's own end follows the #1188 contract:
/// a rollback can be repeated, a token is observed only before a commit or rollback starts, and a
/// transaction the kernel ended under its caller refuses COMMIT with COHDBK001. A started rollback
/// always ends the transaction (#1226), even when its abort record cannot be written.
/// </summary>
public sealed class KeyValueTransactionFailureTests
{
    /// <summary>A failed command, then a write, then ROLLBACK leaves the key space unchanged.</summary>
    /// <param name="failure">The kind of command failure inside the transaction.</param>
    /// <param name="isolation">The isolation level the explicit transaction runs under.</param>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a failed command, a later write and ROLLBACK leave the key space unchanged")]
    [InlineData("conflict", IsolationLevel.Snapshot)]
    [InlineData("parse", IsolationLevel.Snapshot)]
    [InlineData("canceled", IsolationLevel.Snapshot)]
    [InlineData("parse", IsolationLevel.ReadCommitted)]
    [InlineData("canceled", IsolationLevel.ReadCommitted)]
    public async Task Command_FailureThenWriteThenRollback_ShouldLeaveKeySpaceUnchanged(string failure, IsolationLevel isolation)
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await using var other = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("keep"), Bytes("original"), cancellationToken: TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(isolation, TestTimeout.Token());
        if (failure == "conflict")
        {
            // Another session replaces the key after this snapshot began: first-updater-wins.
            await database.PutAsync(other, Bytes("keep"), Bytes("changed"), cancellationToken: TestTimeout.Token());
        }
        await database.PutAsync(session, Bytes("pending"), Bytes("pending"), cancellationToken: TestTimeout.Token());
        KeyValueDatabaseTransaction? blocking = null;
        if (failure == "canceled")
        {
            // Another transaction holds the key's lock, so the failing command waits until canceled.
            blocking = await other.BeginTransactionAsync(TestTimeout.Token());
            await database.PutAsync(other, Bytes("hot"), Bytes("other"), cancellationToken: TestTimeout.Token());
        }

        // Act
        var error = await Should.ThrowAsync<Exception>(async () => await FailAsync(failure, database, session));
        var stateAfterFailure = transaction.State;
        var currentAfterFailure = session.CurrentTransaction;
        await database.PutAsync(session, Bytes("later"), Bytes("later"), cancellationToken: TestTimeout.Token());
        var outside = await database.GetAsync(other, Bytes("later"), TestTimeout.Token());
        await transaction.RollbackAsync(TestTimeout.Token());
        if (blocking is not null)
        {
            await blocking.CommitAsync(TestTimeout.Token());
        }

        // Assert: the failure aborted nothing and autocommitted nothing.
        ShouldBeExpectedFailure(error, failure);
        stateAfterFailure.ShouldBe(TransactionState.Active);
        currentAfterFailure.ShouldBeSameAs(transaction);
        outside.ShouldBeNull();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        Text((await database.GetAsync(observer, Bytes("keep"), TestTimeout.Token()))!.Value.Value)
            .ShouldBe(failure == "conflict" ? "changed" : "original");
        (await Keys(database, observer)).ShouldBe(failure == "canceled" ? ["hot", "keep"] : ["keep"]);
    }

    /// <summary>A command that fails inside its write bracket leaves nothing, and the transaction commits the rest.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a command failing inside its bracket writes nothing and the transaction stays usable")]
    public async Task Command_FailsInsideItsBracket_ShouldWriteNothingAndKeepTransactionUsable()
    {
        // Arrange: both transactions insert one new key; neither snapshot sees the other's insert.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();
        var transactionA = await first.BeginTransactionAsync(TestTimeout.Token());
        var transactionB = await second.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(second, Bytes("before"), Bytes("B"), cancellationToken: TestTimeout.Token());
        (await database.PutAsync(first, Bytes("fresh"), Bytes("from-A"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();
        var blocked = database.PutAsync(second, Bytes("fresh"), Bytes("from-B"), cancellationToken: TestTimeout.Token(30)).AsTask();
        await Task.Delay(100); // B is parked on the key lock before A commits.
        blocked.IsCompleted.ShouldBeFalse();

        // Act: B's insert reaches the primary index inside its bracket, which then refuses the key.
        await transactionA.CommitAsync(TestTimeout.Token());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await blocked);
        var stateAfterFailure = transactionB.State;
        await database.PutAsync(second, Bytes("after"), Bytes("B"), cancellationToken: TestTimeout.Token());
        await transactionB.CommitAsync(TestTimeout.Token());

        // Assert
        stateAfterFailure.ShouldBe(TransactionState.Active);
        await using var observer = await database.CreateSessionAsync();
        Text((await database.GetAsync(observer, Bytes("fresh"), TestTimeout.Token()))!.Value.Value).ShouldBe("from-A");
        (await Keys(database, observer)).ShouldBe(["after", "before", "fresh"]);
    }

    /// <summary>A rollback is idempotent for a transaction that did not commit, and refused for one that did.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: repeated rollback is a no-op; rollback after commit is refused")]
    public async Task RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var rolledBack = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("discarded"), Bytes("v"), cancellationToken: TestTimeout.Token());
        await rolledBack.RollbackAsync(TestTimeout.Token());
        var committed = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("kept"), Bytes("v"), cancellationToken: TestTimeout.Token());
        await committed.CommitAsync(TestTimeout.Token());
        var currentAfterCommit = session.CurrentTransaction;

        // Act
        await rolledBack.RollbackAsync(TestTimeout.Token());
        await rolledBack.DisposeAsync();
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await committed.RollbackAsync(TestTimeout.Token()));

        // Assert: the root base's message (concrete-types plan §6.4), for the model's former
        // "Cannot rollback transaction in state 'Committed': …".
        currentAfterCommit.ShouldBeNull();
        refusal.Message.ShouldBe("The transaction is Committed; a committed transaction cannot roll back.");
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await Keys(database, session)).ShouldBe(["kept"]);
    }

    /// <summary>
    /// A commit of a transaction its caller already rolled back is refused by state, with the root
    /// base's message (concrete-types plan §6.4), for the model's former "Cannot commit transaction
    /// in state 'RolledBack'.".
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: COMMIT after ROLLBACK is refused with the transaction's state")]
    public async Task CommitAsync_AfterRollback_ShouldBeRefusedWithTheState()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("discarded"), Bytes("v"), cancellationToken: TestTimeout.Token());
        await transaction.RollbackAsync(TestTimeout.Token());

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));

        // Assert
        refusal.Message.ShouldBe("The transaction is RolledBack.");
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await Keys(database, session)).ShouldBeEmpty();
    }

    /// <summary>
    /// BEGIN on a session whose transaction is active is refused with the root base's one message,
    /// for the model's former "A transaction is already active on this session.", and the base
    /// checks it before the model's isolation-level refusal (concrete-types plan §6.4, BEGIN's
    /// refusal order): a Serializable BEGIN fails the "already active" way while a transaction is
    /// open, and the Serializable way only when none is.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Session: BEGIN while a transaction is active is refused with one message, before the isolation-level refusal")]
    public async Task BeginTransactionAsync_WhileActive_ShouldBeRefusedBeforeTheIsolationLevel()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());

        // Act
        var again = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(TestTimeout.Token()));
        var serializable = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));
        await transaction.RollbackAsync(TestTimeout.Token());
        var unsupported = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));

        // Assert
        again.Message.ShouldBe("A transaction or operation is already active on this session.");
        serializable.Message.ShouldBe("A transaction or operation is already active on this session.");
        unsupported.Message.ShouldStartWith("IsolationLevel.Serializable is not supported by the key-value engine yet", Case.Sensitive);
        session.CurrentTransaction.ShouldBeNull();
    }

    /// <summary>
    /// A closed session refuses BEGIN and both execute seams with the root base's message, for the
    /// model's former "Session is not open. Current state: Closed.", and before the isolation-level
    /// refusal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Session: a closed session refuses BEGIN and commands with one message")]
    public async Task ClosedSession_ShouldRefuseBeginAndCommands()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var session = await database.CreateSessionAsync();
        await session.DisposeAsync();

        // Act
        var begin = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));
        var typed = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new KeyValueGetRequest(Bytes("k")), TestTimeout.Token()));
        var text = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("GET @k", new Dictionary<string, object?> { ["k"] = Bytes("k") }, TestTimeout.Token()));

        // Assert
        begin.Message.ShouldBe("The session is closed.");
        typed.Message.ShouldBe("The session is closed.");
        text.Message.ShouldBe("The session is closed.");
        session.State.ShouldBe(SessionState.Closed);
    }

    /// <summary>A token canceled before a commit or rollback starts leaves the transaction exactly as it was.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a canceled token never starts a commit or rollback")]
    public async Task EndAsync_TokenCanceledBeforeStart_ShouldLeaveTransactionActive()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("first"), Bytes("v"), cancellationToken: TestTimeout.Token());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.RollbackAsync(canceled.Token));
        await Should.ThrowAsync<OperationCanceledException>(async () => await transaction.CommitAsync(canceled.Token));
        var state = transaction.State;
        await database.PutAsync(session, Bytes("second"), Bytes("v"), cancellationToken: TestTimeout.Token());
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert
        state.ShouldBe(TransactionState.Active);
        transaction.State.ShouldBe(TransactionState.Committed);
        (await Keys(database, session)).ShouldBe(["first", "second"]);
    }

    /// <summary>
    /// A rollback whose abort record cannot be written still ends the transaction and releases its
    /// key locks (#1226). Since #1252 the rollback appends the record to the journal's append
    /// buffer, and it reaches the file with the next drain: here the commit of another session's
    /// put on the key the transaction locked, which proceeds because the lock is free. That drain
    /// fails, which takes the database offline (#1243's rule): the put is reported unconfirmed, the
    /// next command is refused as offline, and the reopen keeps what committed before and nothing
    /// of either transaction.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a rollback whose abort record cannot be written still ends the transaction, and the database goes offline")]
    public async Task RollbackAsync_AbortRecordCannotBeWritten_ShouldEndTransactionAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var (engine, database) = await CreateAsync(options => Quiet(options, strategy));
        var session = await database.CreateSessionAsync();
        var other = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("keep"), Bytes("v"), cancellationToken: TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());

        // A delete of a missing key takes the key's lock and writes no version, so the abort
        // record is the rollback's only journal append.
        (await database.TryDeleteAsync(session, Bytes("missing"), cancellationToken: TestTimeout.Token())).ShouldBeFalse();

        // Act: the rollback buffers the abort record; the other session's put needs the key's
        // lock, and its commit drains the record, which fails.
        int unspentAfterTheRollback;
        int unspent;
        DatabaseTransactionCommitUnconfirmedException lost;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync(TestTimeout.Token());
            unspentAfterTheRollback = failures.Remaining;
            lost = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await database.PutAsync(other, Bytes("missing"), Bytes("other"), cancellationToken: TestTimeout.Token(5)));
            unspent = failures.Remaining;
        }

        var refused = await Should.ThrowAsync<DatabaseOfflineException>(async () =>
            await database.PutAsync(other, Bytes("after"), Bytes("after"), cancellationToken: TestTimeout.Token()));
        await other.DisposeAsync();
        await session.DisposeAsync();
        await engine.DisposeAsync();
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-tests", StorageStrategy = strategy });
        var recovered = await reopened.OpenDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var observer = await recovered.CreateSessionAsync();

        // Assert: the rollback wrote nothing and ended the transaction; the drain that carried its
        // record failed.
        unspentAfterTheRollback.ShouldBe(1);
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        StorageOfflineException.Find(lost).ShouldNotBeNull();
        refused.InnerException.ShouldNotBeNull();
        (await Keys(recovered, observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// After a rollback whose abort record was lost, the transaction is rolled back like any other
    /// and COMMIT commits nothing. The record is lost with the drain that carries it (#1252), here
    /// the commit of the session's next put, which takes the database offline: the COMMIT and a
    /// repeated rollback are then refused as offline before they start.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: COMMIT after a rollback whose abort record was lost is refused")]
    public async Task CommitAsync_AfterRollbackWithLostAbortRecord_ShouldBeRefused()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var (engine, database) = await CreateAsync(options => Quiet(options, strategy));
        var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("keep"), Bytes("v"), cancellationToken: TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        (await database.TryDeleteAsync(session, Bytes("missing"), cancellationToken: TestTimeout.Token())).ShouldBeFalse();
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.RollbackTransaction))
        {
            await transaction.RollbackAsync(TestTimeout.Token());
            await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () =>
                await database.PutAsync(session, Bytes("next"), Bytes("v"), cancellationToken: TestTimeout.Token()));
            unspent = failures.Remaining;
        }

        // Act
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.CommitAsync(TestTimeout.Token()));
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync(TestTimeout.Token()));
        await session.DisposeAsync();
        await engine.DisposeAsync();
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-tests", StorageStrategy = strategy });
        var recovered = await reopened.OpenDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        (await Keys(recovered, observer)).ShouldBe(["keep"]);
    }

    /// <summary>
    /// A commit whose record cannot be written is never acknowledged (#1252): the commit drains the
    /// journal's append buffer through its record before it returns, in every durability mode, and
    /// when that drain fails the database goes offline (#1243's rule) and the commit crosses the
    /// boundary as committed-unconfirmed, its outcome left to the reopen's recovery. The record
    /// never reached the file here, so the reopen holds nothing of the transaction, and a later
    /// rollback is refused as offline.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a commit whose record cannot be written is unconfirmed, and the database goes offline")]
    public async Task CommitAsync_CommitRecordCannotBeWritten_ShouldBeUnconfirmedAndGoOffline()
    {
        // Arrange
        var strategy = new FaultInjectingJournalStorageStrategy();
        var (engine, database) = await CreateAsync(options => Quiet(options, strategy));
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("pending"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Act: the commit's drain carries its commit record, and that write fails.
        DatabaseTransactionCommitUnconfirmedException error;
        int unspent;
        using (var failures = FaultInjectingJournalStorageStrategy.FailJournalWritesContaining(JournalRecordType.CommitTransaction))
        {
            error = await Should.ThrowAsync<DatabaseTransactionCommitUnconfirmedException>(async () => await transaction.CommitAsync(TestTimeout.Token()));
            unspent = failures.Remaining;
        }
        var stateAfterCommit = transaction.State;
        await Should.ThrowAsync<DatabaseOfflineException>(async () => await transaction.RollbackAsync(TestTimeout.Token()));
        await session.DisposeAsync();
        await engine.DisposeAsync();
        await using var reopened = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = "kv-tests", StorageStrategy = strategy });
        var recovered = await reopened.OpenDatabaseAsync(DatabaseName, TestTimeout.Token());
        await using var observer = await recovered.CreateSessionAsync();

        // Assert
        unspent.ShouldBe(0);
        StorageOfflineException.Find(error).ShouldNotBeNull();
        stateAfterCommit.ShouldBe(TransactionState.Committed);
        session.CurrentTransaction.ShouldBeNull();
        (await Keys(recovered, observer)).ShouldBeEmpty();
    }

    // Background workers stay out of the way: a record a test loses must still be in the journal's
    // append buffer when the drain the test fails comes, not written by a worker's drain first.
    private static void Quiet(KeyValueDatabaseEngineOptions options, FaultInjectingJournalStorageStrategy strategy)
    {
        options.StorageStrategy = strategy;
        options.CheckpointInterval = TimeSpan.FromHours(1);
        options.PageWriteBackInterval = TimeSpan.FromHours(1);
        options.MaintenanceInterval = TimeSpan.FromHours(1);
    }

    /// <summary>
    /// Closing a session ends its transaction: the caller's rollback afterwards raises nothing, and
    /// its commit fails with COHDBK001 naming the closure and commits nothing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: closing the session ends the transaction; a later rollback is a no-op and a commit fails with COHDBK001")]
    public async Task DisposeAsync_SessionWithTransaction_ShouldEndTransactionAndAcceptRollback()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("pending"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Act
        await session.DisposeAsync();
        await transaction.RollbackAsync(TestTimeout.Token());
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));

        // Assert
        transaction.State.ShouldBe(TransactionState.RolledBack);
        commit.Message.ShouldStartWith("COHDBK001", Case.Sensitive);
        commit.Message.ShouldContain("nothing was committed", Case.Sensitive);
        commit.Message.ShouldContain("The session closed before the transaction ended.", Case.Sensitive);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        (await Keys(database, observer)).ShouldBeEmpty();
    }

    private static async Task FailAsync(string failure, KeyValueDatabase database, KeyValueDatabaseSession session)
    {
        switch (failure)
        {
            case "conflict":
                await database.PutAsync(session, Bytes("keep"), Bytes("mine"), cancellationToken: TestTimeout.Token());
                break;
            case "parse":
                await session.ExecuteAsync("PUT @k", new Dictionary<string, object?> { ["k"] = Bytes("keep") }, TestTimeout.Token());
                break;
            case "canceled":
            {
                using var cancellation = new CancellationTokenSource();
                var pending = database.PutAsync(session, Bytes("hot"), Bytes("mine"), cancellationToken: cancellation.Token).AsTask();
                await Task.Delay(50);
                pending.IsCompleted.ShouldBeFalse();
                cancellation.Cancel();
                await pending;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    // The failure each case must raise, so a wrong failure cannot pass as the one under test. A
    // canceled task may surface a derived cancellation exception.
    private static void ShouldBeExpectedFailure(Exception error, string failure)
    {
        switch (failure)
        {
            case "conflict":
                error.ShouldBeOfType<DatabaseTransactionAbortedException>();
                break;
            case "parse":
                error.ShouldBeOfType<DatabaseParseException>();
                break;
            case "canceled":
                error.ShouldBeAssignableTo<OperationCanceledException>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private static async Task<List<string>> Keys(KeyValueDatabase database, KeyValueDatabaseSession session)
    {
        var keys = new List<string>();
        await foreach (var entry in database.ScanAsync(session, cancellationToken: TestTimeout.Token()))
        {
            keys.Add(Text(entry.Key));
        }
        return keys.Order(StringComparer.Ordinal).ToList();
    }
}
