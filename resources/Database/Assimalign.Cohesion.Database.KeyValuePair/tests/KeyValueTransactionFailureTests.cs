using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The #1225 audit of the key-value engine. A command is statement-atomic: it writes in one
/// physical bracket that a failure rolls back, so a failed command writes nothing and leaves the
/// explicit transaction active, as a failed SQL statement does; later commands stay inside the
/// transaction and ROLLBACK undoes them. The transaction's own end follows the #1188 contract:
/// a rollback can be repeated, a token is observed only before a commit or rollback starts, and a
/// commit or rollback that does not complete leaves the transaction faulted, refusing commands and
/// BEGIN with COHDBK001 until a rollback completes.
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
        IDatabaseTransaction? blocking = null;
        if (failure == "canceled")
        {
            // Another transaction holds the key's lock, so the failing command waits until canceled.
            blocking = await other.BeginTransactionAsync(TestTimeout.Token());
            await database.PutAsync(other, Bytes("hot"), Bytes("other"), cancellationToken: TestTimeout.Token());
        }

        // Act
        await Should.ThrowAsync<Exception>(async () => await FailAsync(failure, database, session));
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

        // Assert
        currentAfterCommit.ShouldBeNull();
        refusal.Message.ShouldContain("Committed", Case.Sensitive);
        rolledBack.State.ShouldBe(TransactionState.RolledBack);
        committed.State.ShouldBe(TransactionState.Committed);
        (await Keys(database, session)).ShouldBe(["kept"]);
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

    /// <summary>A rollback that does not complete leaves the transaction faulted and refusing work until a rollback completes.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a rollback that does not complete leaves the transaction faulted")]
    public async Task RollbackAsync_ThatDoesNotComplete_ShouldLeaveTransactionFaultedUntilRetried()
    {
        // Arrange
        var (engine, database) = await CreateAsync(options => options.StorageStrategy = new FaultInjectingJournalStorageStrategy());
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("keep"), Bytes("v"), cancellationToken: TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        (await database.GetAsync(session, Bytes("keep"), TestTimeout.Token())).ShouldNotBeNull();

        // Act: the abort record is the rollback's only journal write, so failing it fails the rollback.
        IOException rollbackFailure;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            rollbackFailure = await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync(TestTimeout.Token()));
        }
        var faultedState = transaction.State;
        var currentWhileFaulted = session.CurrentTransaction;
        var refused = await Should.ThrowAsync<DatabaseException>(async () =>
            await database.PutAsync(session, Bytes("late"), Bytes("v"), cancellationToken: TestTimeout.Token()));
        var refusedText = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("KEYSPACES", cancellationToken: TestTimeout.Token()));
        var beginRefused = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(TestTimeout.Token()));
        await transaction.RollbackAsync(TestTimeout.Token());

        // Assert
        faultedState.ShouldBe(TransactionState.Faulted);
        currentWhileFaulted.ShouldBeSameAs(transaction);
        refused.Message.ShouldStartWith("COHDBK001", Case.Sensitive);
        refused.Message.ShouldContain("did not complete", Case.Sensitive);
        refused.InnerException.ShouldBeSameAs(rollbackFailure);
        refusedText.Message.ShouldStartWith("COHDBK001", Case.Sensitive);
        beginRefused.Message.ShouldStartWith("COHDBK001", Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await database.PutAsync(session, Bytes("after"), Bytes("v"), cancellationToken: TestTimeout.Token());
        (await Keys(database, session)).ShouldBe(["after", "keep"]);
    }

    /// <summary>COMMIT after a rollback that did not complete fails with COHDBK001, commits nothing, and ends the transaction.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: COMMIT after an incomplete rollback fails and ends the transaction")]
    public async Task CommitAsync_AfterRollbackThatDidNotComplete_ShouldFailAndEndTransaction()
    {
        // Arrange
        var (engine, database) = await CreateAsync(options => options.StorageStrategy = new FaultInjectingJournalStorageStrategy());
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        (await database.GetAsync(session, Bytes("missing"), TestTimeout.Token())).ShouldBeNull();
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            await Should.ThrowAsync<IOException>(async () => await transaction.RollbackAsync(TestTimeout.Token()));
        }

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));

        // Assert
        error.Message.ShouldStartWith("COHDBK001", Case.Sensitive);
        error.Message.ShouldContain("nothing was committed", Case.Sensitive);
        error.InnerException.ShouldBeOfType<IOException>();
        transaction.State.ShouldBe(TransactionState.RolledBack);
        session.CurrentTransaction.ShouldBeNull();
        await transaction.RollbackAsync(TestTimeout.Token());
    }

    /// <summary>A commit the kernel aborts crosses the boundary translated, and a catch-block rollback afterwards raises nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: a kernel-aborted commit is translated and a later rollback is a no-op")]
    public async Task CommitAsync_KernelAbortsCommit_ShouldTranslateAndAcceptRollback()
    {
        // Arrange
        var (engine, database) = await CreateAsync(options => options.StorageStrategy = new FaultInjectingJournalStorageStrategy());
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("pending"), Bytes("v"), cancellationToken: TestTimeout.Token());

        // Act: the commit record is the commit's first journal write; the kernel then aborts the transaction.
        DatabaseTransactionAbortedException error;
        using (FaultInjectingJournalStorageStrategy.FailJournalWrites(1))
        {
            error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await transaction.CommitAsync(TestTimeout.Token()));
        }
        var stateAfterCommit = transaction.State;
        await transaction.RollbackAsync(TestTimeout.Token());

        // Assert
        error.InnerException.ShouldBeOfType<TransactionAbortedException>();
        stateAfterCommit.ShouldBe(TransactionState.Faulted);
        transaction.State.ShouldBe(TransactionState.Faulted);
        session.CurrentTransaction.ShouldBeNull();
        await database.PutAsync(session, Bytes("after"), Bytes("v"), cancellationToken: TestTimeout.Token());
        (await Keys(database, session)).ShouldBe(["after"]);
    }

    /// <summary>Closing a session ends its transaction, and the caller's rollback afterwards raises nothing.</summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Transaction: closing the session ends the transaction and a later rollback is a no-op")]
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
        commit.Message.ShouldContain("RolledBack", Case.Sensitive);
        session.CurrentTransaction.ShouldBeNull();
        await using var observer = await database.CreateSessionAsync();
        (await Keys(database, observer)).ShouldBeEmpty();
    }

    private static async Task FailAsync(string failure, IKeyValueDatabase database, IDatabaseSession session)
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

    private static async Task<List<string>> Keys(IKeyValueDatabase database, IDatabaseSession session)
    {
        var keys = new List<string>();
        await foreach (var entry in database.ScanAsync(session, cancellationToken: TestTimeout.Token()))
        {
            keys.Add(Text(entry.Key));
        }
        return keys.Order(StringComparer.Ordinal).ToList();
    }
}
