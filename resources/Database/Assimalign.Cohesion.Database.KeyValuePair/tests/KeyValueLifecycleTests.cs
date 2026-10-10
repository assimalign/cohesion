using System;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
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
    /// The #1225 and #1226 end rules meet: a rollback whose undo the journal rejects ends the
    /// transaction but defers the undo, so the transaction keeps the key locks of what it wrote
    /// until the purge pass completes the undo (#1226). Its command parked on another key's lock
    /// still fails at once (#1225): the deferred end fails the request it finds queued, as the
    /// release at any other end does, instead of leaving it queued until the blocker ends.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Lifecycle: a rollback whose undo is deferred fails a parked command at once and keeps its written keys locked")]
    public async Task PutAsync_RollbackDefersUndoWhileCommandWaitsForKeyLock_ShouldFailCommandAtOnceAndHoldWrittenKeys()
    {
        // Arrange: the blocker holds the lock of a fresh key; the waiter wrote another key first.
        var (engine, database) = await CreateAsync(options =>
        {
            // The engine's maintenance stays out of the way, the deferred-undo retry included:
            // the test drives the purge pass itself.
            options.StorageStrategy = new FaultInjectingJournalStorageStrategy();
            options.MaintenanceInterval = TimeSpan.FromHours(1);
            options.CheckpointInterval = TimeSpan.FromHours(1);
            options.DeferredUndoRetryDelay = TimeSpan.FromHours(1);
        });
        await using var _ = engine;
        await using var waitingSession = await database.CreateSessionAsync();
        await using var blockingSession = await database.CreateSessionAsync();
        await using var observer = await database.CreateSessionAsync();
        var blocker = await blockingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(blockingSession, Bytes("hot"), Bytes("blocker"), cancellationToken: TestTimeout.Token());
        var waiting = await waitingSession.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(waitingSession, Bytes("earlier"), Bytes("waiter"), cancellationToken: TestTimeout.Token());
        var pending = database.PutAsync(waitingSession, Bytes("hot"), Bytes("waiter"), cancellationToken: TestTimeout.Token(30)).AsTask();
        bool parked = !pending.IsCompleted;

        // Act: another storage bracket holds every page while the rollback runs, so the undo's
        // bracket cannot touch the first page it undoes: the kernel ends the transaction and
        // defers the undo. (Until #1252 a failed journal write was the fault; a journal write
        // failure now takes the database offline.)
        int locked;
        using (var holder = PageWriteLockHolder.LockEveryPage(database.DataStorage))
        {
            await waiting.RollbackAsync(TestTimeout.Token());
            locked = holder.Pages;
        }
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending.WaitAsync(TestTimeout.Token()));
        var blockerStateWhenTheCommandFailed = blocker.State;
        int deferred = database.Coordinator.VersionStore.PendingAbortedPurges.Count;
        var overwrite = database.PutAsync(observer, Bytes("earlier"), Bytes("observer"), cancellationToken: TestTimeout.Token(30)).AsTask();
        await Task.WhenAny(overwrite, Task.Delay(TimeSpan.FromMilliseconds(250)));
        bool overwroteBeforeTheUndo = overwrite.IsCompleted;
        database.Coordinator.RunVersionPurgePass(TestTimeout.Token());
        (await overwrite.WaitAsync(TestTimeout.Token())).Applied.ShouldBeTrue();
        await blocker.CommitAsync(TestTimeout.Token());

        // Assert
        parked.ShouldBeTrue();
        locked.ShouldBeGreaterThan(0);
        deferred.ShouldBe(1);
        waiting.State.ShouldBe(TransactionState.RolledBack);
        error.InnerException.ShouldBeOfType<TransactionAbortedException>();
        error.Message.ShouldContain("ended while it waited", Case.Sensitive);
        blockerStateWhenTheCommandFailed.ShouldBe(TransactionState.Active);
        overwroteBeforeTheUndo.ShouldBeFalse();
        database.Coordinator.VersionStore.PendingAbortedPurges.ShouldBeEmpty();
        Text((await database.GetAsync(observer, Bytes("earlier"), TestTimeout.Token())).ShouldNotBeNull().Value).ShouldBe("observer");
        Text((await database.GetAsync(observer, Bytes("hot"), TestTimeout.Token())).ShouldNotBeNull().Value).ShouldBe("blocker");
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
        // The root base's message (concrete-types plan §6.4), for the model's former "A command of
        // the transaction is still running; commit after it completes.".
        refused.Message.ShouldBe("An operation of the transaction is still running; commit after it completes.");
        stateAfterRefusal.ShouldBe(TransactionState.Active);
        written.Applied.ShouldBeTrue();
        transaction.State.ShouldBe(TransactionState.Committed);
        Text(hot.ShouldNotBeNull().Value).ShouldBe("mine");
    }
}
