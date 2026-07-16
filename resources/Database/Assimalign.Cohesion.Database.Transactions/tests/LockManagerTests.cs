using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the lock manager: the compatibility matrix, blocking waits with FIFO
/// wake-up, upgrades, cancellation, and deadlock victim resolution (#850).
/// </summary>
public class LockManagerTests
{
    private static readonly TransactionSequence T1 = new(1);
    private static readonly TransactionSequence T2 = new(2);
    private static readonly TransactionSequence T3 = new(3);

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Shared locks are mutually compatible")]
    public async Task AcquireAsync_SharedWithShared_ShouldGrantImmediately()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(1, 1);

        // Act / Assert: both complete synchronously without waiting.
        await locks.AcquireAsync(T1, resource, LockMode.Shared);
        await locks.AcquireAsync(T2, resource, LockMode.Shared);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Intent modes follow the hierarchy matrix")]
    public void TryAcquire_IntentModes_ShouldFollowMatrix()
    {
        var locks = LockManager.Create();
        var table = LockResource.Object(10);

        locks.TryAcquire(T1, table, LockMode.IntentExclusive).ShouldBeTrue();
        locks.TryAcquire(T2, table, LockMode.IntentShared).ShouldBeTrue();  // IX ~ IS compatible
        locks.TryAcquire(T3, table, LockMode.Shared).ShouldBeFalse();       // IX ~ S incompatible
        locks.TryAcquire(T3, table, LockMode.Exclusive).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Exclusive blocks until released")]
    public async Task AcquireAsync_ExclusiveHeld_ShouldBlockUntilReleaseAll()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(1, 5);
        await locks.AcquireAsync(T1, resource, LockMode.Exclusive);

        // Act
        var waiting = locks.AcquireAsync(T2, resource, LockMode.Shared).AsTask();
        await Task.Delay(50);
        waiting.IsCompleted.ShouldBeFalse();

        locks.ReleaseAll(T1);

        // Assert
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: TryAcquire never waits")]
    public async Task TryAcquire_Conflicting_ShouldReturnFalse()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(2, 2);
        await locks.AcquireAsync(T1, resource, LockMode.Exclusive);

        // Act / Assert
        locks.TryAcquire(T2, resource, LockMode.Shared).ShouldBeFalse();
        locks.TryAcquire(T1, resource, LockMode.Exclusive).ShouldBeTrue(); // own grant
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Sole holder can upgrade shared to exclusive")]
    public async Task AcquireAsync_SoleHolderUpgrade_ShouldGrant()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(3, 3);
        await locks.AcquireAsync(T1, resource, LockMode.Shared);

        // Act: upgrade while no other holder exists.
        await locks.AcquireAsync(T1, resource, LockMode.Exclusive);

        // Assert: the upgraded lock now blocks others.
        locks.TryAcquire(T2, resource, LockMode.Shared).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Deadlock aborts the requester that closes the cycle")]
    public async Task AcquireAsync_CircularWait_ShouldAbortVictim()
    {
        // Arrange: T1 holds A, T2 holds B, T1 waits on B.
        var locks = LockManager.Create();
        var resourceA = LockResource.Entry(1, 100);
        var resourceB = LockResource.Entry(1, 200);

        await locks.AcquireAsync(T1, resourceA, LockMode.Exclusive);
        await locks.AcquireAsync(T2, resourceB, LockMode.Exclusive);

        var firstWait = locks.AcquireAsync(T1, resourceB, LockMode.Exclusive).AsTask();
        await Task.Delay(50);
        firstWait.IsCompleted.ShouldBeFalse();

        // Act / Assert: T2 requesting A closes the cycle and is the victim.
        await Should.ThrowAsync<TransactionDeadlockException>(
            async () => await locks.AcquireAsync(T2, resourceA, LockMode.Exclusive));

        // The victim aborts (releases everything) and the survivor proceeds.
        locks.ReleaseAll(T2);
        await firstWait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Cancellation abandons the wait")]
    public async Task AcquireAsync_Cancelled_ShouldAbandonWait()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(4, 4);
        await locks.AcquireAsync(T1, resource, LockMode.Exclusive);

        using var cancellation = new CancellationTokenSource();
        var waiting = locks.AcquireAsync(T2, resource, LockMode.Shared, cancellation.Token).AsTask();

        // Act
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        // The abandoned wait must not receive the lock on release.
        locks.ReleaseAll(T1);
        locks.TryAcquire(T3, resource, LockMode.Exclusive).ShouldBeTrue();
    }
}
