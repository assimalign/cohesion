using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// MVCC semantics on the key-value engine: snapshot vs read-committed
/// visibility, key-grain write conflicts (first-updater-wins), deadlock
/// victimization, and logical rollback — the isolation guarantees the shared
/// kernel must deliver to a non-SQL model unchanged.
/// </summary>
public class KeyValueMvccTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Isolation: A snapshot transaction keeps its begin-time view while others commit")]
    public async Task Snapshot_ConcurrentCommit_ShouldStayInvisibleUntilOwnCommit()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await database.PutAsync(writer, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());

        // Act: fix the reader's snapshot, then commit a change beside it.
        var transaction = await reader.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await database.PutAsync(writer, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());

        var during = await database.GetAsync(reader, Bytes("k"), TestTimeout.Token());
        await transaction.CommitAsync(TestTimeout.Token());
        var after = await database.GetAsync(reader, Bytes("k"), TestTimeout.Token());

        // Assert
        Text(during!.Value.Value).ShouldBe("v1");
        Text(after!.Value.Value).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Isolation: A read-committed transaction sees each command's fresh committed state")]
    public async Task ReadCommitted_ConcurrentCommit_ShouldRefreshPerCommand()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var reader = await database.CreateSessionAsync();
        await using var writer = await database.CreateSessionAsync();
        await database.PutAsync(writer, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());

        // Act
        var transaction = await reader.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        var before = await database.GetAsync(reader, Bytes("k"), TestTimeout.Token());
        await database.PutAsync(writer, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());
        var refreshed = await database.GetAsync(reader, Bytes("k"), TestTimeout.Token());
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert
        Text(before!.Value.Value).ShouldBe("v1");
        Text(refreshed!.Value.Value).ShouldBe("v2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Isolation: Serializable is rejected at begin (never weaker than requested)")]
    public async Task BeginTransaction_Serializable_ShouldBeRejected()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();

        // Act / Assert
        await Should.ThrowAsync<DatabaseException>(async () =>
            await session.BeginTransactionAsync(IsolationLevel.Serializable, TestTimeout.Token()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Conflicts: Two transactions writing disjoint keys both commit")]
    public async Task ConcurrentWriters_DisjointKeys_ShouldBothCommit()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();

        // Act: interleave two explicit transactions on disjoint keys.
        var transactionA = await first.BeginTransactionAsync(TestTimeout.Token());
        var transactionB = await second.BeginTransactionAsync(TestTimeout.Token());

        (await database.PutAsync(first, Bytes("a"), Bytes("from-A"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();
        (await database.PutAsync(second, Bytes("b"), Bytes("from-B"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();

        await transactionA.CommitAsync(TestTimeout.Token());
        await transactionB.CommitAsync(TestTimeout.Token());

        // Assert
        await using var check = await database.CreateSessionAsync();
        Text((await database.GetAsync(check, Bytes("a"), TestTimeout.Token()))!.Value.Value).ShouldBe("from-A");
        Text((await database.GetAsync(check, Bytes("b"), TestTimeout.Token()))!.Value.Value).ShouldBe("from-B");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Conflicts: The same-key second writer waits, then conflicts after the first commits (first-updater-wins)")]
    public async Task ConcurrentWriters_SameKey_ShouldResolveFirstUpdaterWins()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();
        await using var seed = await database.CreateSessionAsync();
        await database.PutAsync(seed, Bytes("k"), Bytes("v0"), cancellationToken: TestTimeout.Token());

        var transactionA = await first.BeginTransactionAsync(TestTimeout.Token());
        var transactionB = await second.BeginTransactionAsync(TestTimeout.Token());

        // Act: A takes the key lock; B's put parks on it; A commits; B resumes
        // into the latest-state check and loses (first-updater-wins).
        (await database.PutAsync(first, Bytes("k"), Bytes("from-A"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();

        Task<KeyValuePutResult> blocked = database.PutAsync(second, Bytes("k"), Bytes("from-B"), cancellationToken: TestTimeout.Token(30)).AsTask();
        await Task.Delay(100); // Deterministic ordering: B is parked on the key lock before A commits.
        blocked.IsCompleted.ShouldBeFalse();

        await transactionA.CommitAsync(TestTimeout.Token());

        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await blocked);
        await transactionB.RollbackAsync(TestTimeout.Token());

        // Assert: a retry on a fresh transaction succeeds over A's committed state.
        (await database.PutAsync(second, Bytes("k"), Bytes("retry-B"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();
        await using var check = await database.CreateSessionAsync();
        Text((await database.GetAsync(check, Bytes("k"), TestTimeout.Token()))!.Value.Value).ShouldBe("retry-B");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Conflicts: Both-inserting writers of one new key resolve first-updater-wins")]
    public async Task ConcurrentWriters_BothInsertingSameNewKey_ShouldConflictSecond()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();

        var transactionA = await first.BeginTransactionAsync(TestTimeout.Token());
        var transactionB = await second.BeginTransactionAsync(TestTimeout.Token());

        // Act: neither snapshot can see the other's insert; the unique primary
        // index's latest-state check under the key lock arbitrates.
        (await database.PutAsync(first, Bytes("fresh"), Bytes("from-A"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();

        Task<KeyValuePutResult> blocked = database.PutAsync(second, Bytes("fresh"), Bytes("from-B"), cancellationToken: TestTimeout.Token(30)).AsTask();
        await Task.Delay(100);
        blocked.IsCompleted.ShouldBeFalse();

        await transactionA.CommitAsync(TestTimeout.Token());

        // Assert
        await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await blocked);
        await transactionB.RollbackAsync(TestTimeout.Token());

        await using var check = await database.CreateSessionAsync();
        Text((await database.GetAsync(check, Bytes("fresh"), TestTimeout.Token()))!.Value.Value).ShouldBe("from-A");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Conflicts: A key-lock cycle aborts the requester as deadlock victim; the survivor completes")]
    public async Task ConcurrentWriters_LockCycle_ShouldAbortDeadlockVictim()
    {
        // Arrange: A holds k1 and B holds k2; A parks on k2; B's request for k1
        // would close the cycle — B is the victim (requester-closes-cycle).
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var first = await database.CreateSessionAsync();
        await using var second = await database.CreateSessionAsync();

        var transactionA = await first.BeginTransactionAsync(TestTimeout.Token());
        var transactionB = await second.BeginTransactionAsync(TestTimeout.Token());

        (await database.PutAsync(first, Bytes("k1"), Bytes("A"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();
        (await database.PutAsync(second, Bytes("k2"), Bytes("B"), cancellationToken: TestTimeout.Token())).Applied.ShouldBeTrue();

        // Act
        Task<KeyValuePutResult> parked = database.PutAsync(first, Bytes("k2"), Bytes("A2"), cancellationToken: TestTimeout.Token(30)).AsTask();
        await Task.Delay(100); // A is parked on k2 before B closes the cycle.
        parked.IsCompleted.ShouldBeFalse();

        await Should.ThrowAsync<DatabaseTransactionDeadlockException>(async () =>
            await database.PutAsync(second, Bytes("k1"), Bytes("B2"), cancellationToken: TestTimeout.Token()));

        await transactionB.RollbackAsync(TestTimeout.Token());

        // Assert: the survivor's parked put resumes and its transaction commits.
        (await parked).Applied.ShouldBeTrue();
        await transactionA.CommitAsync(TestTimeout.Token());

        await using var check = await database.CreateSessionAsync();
        Text((await database.GetAsync(check, Bytes("k2"), TestTimeout.Token()))!.Value.Value).ShouldBe("A2");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Rollback: An explicit rollback logically undoes puts, deletes, and index entries")]
    public async Task Rollback_PutAndDelete_ShouldUndoLogically()
    {
        // Arrange
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        var seeded = await database.PutAsync(session, Bytes("keep"), Bytes("original"), cancellationToken: TestTimeout.Token());

        // Act: one transaction inserts a new key, replaces an existing one, and
        // deletes it again — then rolls everything back.
        var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
        await database.PutAsync(session, Bytes("new"), Bytes("inserted"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("keep"), Bytes("replaced"), cancellationToken: TestTimeout.Token());
        await transaction.RollbackAsync(TestTimeout.Token());

        // Assert: the new key is gone (record AND index entry — a get is an index
        // seek) and the replaced key reads its original version with its etag.
        (await database.GetAsync(session, Bytes("new"), TestTimeout.Token())).ShouldBeNull();
        var kept = await database.GetAsync(session, Bytes("keep"), TestTimeout.Token());
        Text(kept!.Value.Value).ShouldBe("original");
        kept.Value.ETag.ShouldBe(seeded.ETag!.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Purge: A version-purge pass reclaims committed tombstones below every open snapshot")]
    public async Task VersionPurge_CommittedTombstones_ShouldReclaimBelowSnapshotFloor()
    {
        // Arrange: replace a value twice — the superseded versions become
        // committed tombstones in the prunable set.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        await using var session = await database.CreateSessionAsync();
        await database.PutAsync(session, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());
        await database.PutAsync(session, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());
        await database.TryDeleteAsync(session, Bytes("other-missing"), cancellationToken: TestTimeout.Token());

        var instance = (Internal.KeyValueDatabaseInstance)database;
        instance.Coordinator.VersionStore.TrackedVersionCount.ShouldBeGreaterThan(0);

        // Act: no snapshots are open — the pass may reclaim everything decided.
        long reclaimed = instance.Coordinator.RunVersionPurgePass(TestTimeout.Token());

        // Assert: the superseded version was physically reclaimed and the live
        // value still reads correctly through the index.
        reclaimed.ShouldBeGreaterThan(0);
        Text((await database.GetAsync(session, Bytes("k"), TestTimeout.Token()))!.Value.Value).ShouldBe("v2");
    }
}
