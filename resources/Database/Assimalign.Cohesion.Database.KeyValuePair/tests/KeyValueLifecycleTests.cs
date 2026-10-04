using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// A key-value transaction can end on another thread while one of its commands still runs: the
/// caller's rollback, or the session closing. A command parked on a key lock then fails at once,
/// writes nothing, and never keeps the key lock, so a rolled-back write never becomes visible and
/// later writers of the key are not blocked (#1225 review). A commit, by contrast, waits for no
/// command: one that starts while a command of the transaction runs is refused.
/// </summary>
public sealed class KeyValueLifecycleTests
{
    /// <summary>
    /// A PUT parked on a key lock when its transaction ends fails at once and writes nothing; the
    /// key's next writer gets the lock whether the blocking transaction commits or rolls back.
    /// </summary>
    /// <param name="disposeSession">True to end the waiter by closing its session; false to roll its transaction back.</param>
    /// <param name="isolation">The waiter's isolation level.</param>
    /// <param name="blockerCommits">True when the transaction holding the key lock commits; false when it rolls back.</param>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Lifecycle: a command parked on a key lock fails when its transaction ends and writes nothing")]
    [InlineData(false, IsolationLevel.Snapshot, true)]
    [InlineData(false, IsolationLevel.Snapshot, false)]
    [InlineData(false, IsolationLevel.ReadCommitted, true)]
    [InlineData(false, IsolationLevel.ReadCommitted, false)]
    [InlineData(true, IsolationLevel.Snapshot, true)]
    [InlineData(true, IsolationLevel.Snapshot, false)]
    [InlineData(true, IsolationLevel.ReadCommitted, true)]
    [InlineData(true, IsolationLevel.ReadCommitted, false)]
    public async Task PutAsync_TransactionEndsWhileWaitingForKeyLock_ShouldFailAndWriteNothing(bool disposeSession, IsolationLevel isolation, bool blockerCommits)
    {
        // Arrange: the blocker holds the lock of a fresh key; the waiter wrote another key first.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var waitingSession = await database.CreateSessionAsync();
        await using var blockingSession = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        var blocker = await blockingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(blockingSession, Bytes("hot"), Bytes("blocker"), cancellationToken: TestTimeout.Token());
        var waiting = await waitingSession.BeginTransactionAsync(isolation, TestTimeout.Token());
        await database.PutAsync(waitingSession, Bytes("earlier"), Bytes("waiter"), cancellationToken: TestTimeout.Token());
        var pending = database.PutAsync(waitingSession, Bytes("hot"), Bytes("waiter"), cancellationToken: TestTimeout.Token(30)).AsTask();
        bool parked = !pending.IsCompleted;

        // Act: the parked command fails as its transaction ends, before the blocker does.
        if (disposeSession)
        {
            await waitingSession.DisposeAsync();
        }
        else
        {
            await waiting.RollbackAsync(TestTimeout.Token());
        }
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(TestTimeout.Token()));
        if (blockerCommits)
        {
            await blocker.CommitAsync(TestTimeout.Token());
        }
        else
        {
            await blocker.RollbackAsync(TestTimeout.Token());
        }
        var hot = await database.GetAsync(observer, Bytes("hot"), TestTimeout.Token());
        var earlier = await database.GetAsync(observer, Bytes("earlier"), TestTimeout.Token());
        var later = await database.PutAsync(observer, Bytes("hot"), Bytes("later"), cancellationToken: TestTimeout.Token(5));

        // Assert
        parked.ShouldBeTrue();
        waiting.State.ShouldBe(TransactionState.RolledBack);
        error.InnerException.ShouldBeOfType<TransactionAbortedException>();
        if (blockerCommits)
        {
            Text(hot.ShouldNotBeNull().Value).ShouldBe("blocker");
        }
        else
        {
            hot.ShouldBeNull();
        }
        earlier.ShouldBeNull();
        later.Applied.ShouldBeTrue();
        await waitingSession.DisposeAsync();
    }

    /// <summary>
    /// A DELETE parked on a key lock when its transaction rolls back fails and leaves the key as
    /// the blocking transaction committed it, and the key's next writer is not blocked.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Lifecycle: a parked delete fails when its transaction ends and deletes nothing")]
    public async Task DeleteAsync_TransactionEndsWhileWaitingForKeyLock_ShouldFailAndDeleteNothing()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var waitingSession = await database.CreateSessionAsync();
        await using var blockingSession = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        await database.PutAsync(observer, Bytes("hot"), Bytes("seed"), cancellationToken: TestTimeout.Token());
        var blocker = await blockingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(blockingSession, Bytes("hot"), Bytes("blocker"), cancellationToken: TestTimeout.Token());
        var waiting = await waitingSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        var pending = database.TryDeleteAsync(waitingSession, Bytes("hot"), cancellationToken: TestTimeout.Token(30)).AsTask();
        bool parked = !pending.IsCompleted;

        // Act
        await waiting.RollbackAsync(TestTimeout.Token());
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(TestTimeout.Token()));
        await blocker.CommitAsync(TestTimeout.Token());
        var hot = await database.GetAsync(observer, Bytes("hot"), TestTimeout.Token());
        bool deletedLater = await database.TryDeleteAsync(observer, Bytes("hot"), cancellationToken: TestTimeout.Token(5));

        // Assert
        parked.ShouldBeTrue();
        Text(hot.ShouldNotBeNull().Value).ShouldBe("blocker");
        deletedLater.ShouldBeTrue();
    }

    /// <summary>
    /// A commit that starts while a command of the transaction is still running is refused and
    /// leaves the transaction active; once the command completes, the commit succeeds.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Lifecycle: COMMIT while a command runs is refused and leaves the transaction active")]
    public async Task CommitAsync_WhileCommandRuns_ShouldBeRefusedAndKeepTransaction()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await using var blockingSession = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        var blocker = await blockingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(blockingSession, Bytes("hot"), Bytes("blocker"), cancellationToken: TestTimeout.Token());
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        var pending = database.PutAsync(session, Bytes("hot"), Bytes("mine"), cancellationToken: TestTimeout.Token(30)).AsTask();
        bool parked = !pending.IsCompleted;

        // Act
        var refused = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(TestTimeout.Token()));
        var stateAfterRefusal = transaction.State;
        await blocker.RollbackAsync(TestTimeout.Token());
        var written = await pending.WaitAsync(TestTimeout.Token());
        await transaction.CommitAsync(TestTimeout.Token());
        var hot = await database.GetAsync(observer, Bytes("hot"), TestTimeout.Token());

        // Assert
        parked.ShouldBeTrue();
        refused.Message.ShouldContain("still running", Case.Sensitive);
        refused.Message.ShouldNotStartWith("COHDBK001", Case.Sensitive);
        stateAfterRefusal.ShouldBe(TransactionState.Active);
        written.Applied.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.Committed);
        Text(hot.ShouldNotBeNull().Value).ShouldBe("mine");
    }
}
