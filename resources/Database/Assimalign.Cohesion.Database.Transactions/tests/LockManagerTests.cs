using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the lock manager: the compatibility matrix, blocking waits with FIFO
/// wake-up, upgrades, cancellation, deadlock victim resolution (#850, #1390), and an ended
/// owner's queued requests (#1225 review).
/// </summary>
public class LockManagerTests
{
    private static readonly TransactionSequence _t1 = new(1);
    private static readonly TransactionSequence _t2 = new(2);
    private static readonly TransactionSequence _t3 = new(3);

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Shared locks are mutually compatible")]
    public async Task AcquireAsync_SharedWithShared_ShouldGrantImmediately()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(1, 1);

        // Act / Assert: both complete synchronously without waiting.
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        await locks.AcquireAsync(_t2, resource, LockMode.Shared);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Intent modes follow the hierarchy matrix")]
    public void TryAcquire_IntentModes_ShouldFollowMatrix()
    {
        var locks = LockManager.Create();
        var table = LockResource.Object(10);

        locks.TryAcquire(_t1, table, LockMode.IntentExclusive).ShouldBeTrue();
        locks.TryAcquire(_t2, table, LockMode.IntentShared).ShouldBeTrue();  // IX ~ IS compatible
        locks.TryAcquire(_t3, table, LockMode.Shared).ShouldBeFalse();       // IX ~ S incompatible
        locks.TryAcquire(_t3, table, LockMode.Exclusive).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Exclusive blocks until released")]
    public async Task AcquireAsync_ExclusiveHeld_ShouldBlockUntilReleaseAll()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(1, 5);
        await locks.AcquireAsync(_t1, resource, LockMode.Exclusive);

        // Act
        var waiting = locks.AcquireAsync(_t2, resource, LockMode.Shared).AsTask();
        await Task.Delay(50);
        waiting.IsCompleted.ShouldBeFalse();

        locks.ReleaseAll(_t1);

        // Assert
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: TryAcquire never waits")]
    public async Task TryAcquire_Conflicting_ShouldReturnFalse()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(2, 2);
        await locks.AcquireAsync(_t1, resource, LockMode.Exclusive);

        // Act / Assert
        locks.TryAcquire(_t2, resource, LockMode.Shared).ShouldBeFalse();
        locks.TryAcquire(_t1, resource, LockMode.Exclusive).ShouldBeTrue(); // own grant
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Sole holder can upgrade shared to exclusive")]
    public async Task AcquireAsync_SoleHolderUpgrade_ShouldGrant()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(3, 3);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);

        // Act: upgrade while no other holder exists.
        await locks.AcquireAsync(_t1, resource, LockMode.Exclusive);

        // Assert: the upgraded lock now blocks others.
        locks.TryAcquire(_t2, resource, LockMode.Shared).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Deadlock aborts the requester that closes the cycle")]
    public async Task AcquireAsync_CircularWait_ShouldAbortVictim()
    {
        // Arrange: T1 holds A, T2 holds B, T1 waits on B.
        var locks = LockManager.Create();
        var resourceA = LockResource.Entry(1, 100);
        var resourceB = LockResource.Entry(1, 200);

        await locks.AcquireAsync(_t1, resourceA, LockMode.Exclusive);
        await locks.AcquireAsync(_t2, resourceB, LockMode.Exclusive);

        var firstWait = locks.AcquireAsync(_t1, resourceB, LockMode.Exclusive).AsTask();
        await Task.Delay(50);
        firstWait.IsCompleted.ShouldBeFalse();

        // Act / Assert: T2 requesting A closes the cycle and is the victim.
        await Should.ThrowAsync<TransactionDeadlockException>(
            async () => await locks.AcquireAsync(_t2, resourceA, LockMode.Exclusive));

        // The victim aborts (releases everything) and the survivor proceeds.
        locks.ReleaseAll(_t2);
        await firstWait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Cancellation abandons the wait")]
    public async Task AcquireAsync_Cancelled_ShouldAbandonWait()
    {
        // Arrange
        var locks = LockManager.Create();
        var resource = LockResource.Entry(4, 4);
        await locks.AcquireAsync(_t1, resource, LockMode.Exclusive);

        using var cancellation = new CancellationTokenSource();
        var waiting = locks.AcquireAsync(_t2, resource, LockMode.Shared, cancellation.Token).AsTask();

        // Act
        cancellation.Cancel();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));

        // The abandoned wait must not receive the lock on release.
        locks.ReleaseAll(_t1);
        locks.TryAcquire(_t3, resource, LockMode.Exclusive).ShouldBeTrue();
    }

    /// <summary>
    /// Releasing an owner fails the owner's own queued requests: a grant that arrived after the
    /// owner ended would hold the resource for a transaction that can never release it again.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: ReleaseAll fails the owner's queued requests")]
    public async Task ReleaseAll_OwnerWithQueuedRequest_ShouldFailTheRequestAndNeverGrantIt()
    {
        // Arrange: T1 holds the resource; T2 waits for it.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(5, 5);
        await locks.AcquireAsync(_t1, resource, LockMode.Exclusive);
        var waiting = locks.AcquireAsync(_t2, resource, LockMode.Exclusive).AsTask();
        waiting.IsCompleted.ShouldBeFalse();

        // Act: T2 ends while it waits, then T1 releases.
        locks.ReleaseAll(_t2);
        var error = await Should.ThrowAsync<TransactionAbortedException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        locks.ReleaseAll(_t1);

        // Assert: the ended request was never granted, so a third owner gets the resource at once.
        error.Message.ShouldStartWith($"Transaction {_t2} ended while it waited", Case.Sensitive);
        locks.TryAcquire(_t3, resource, LockMode.Exclusive).ShouldBeTrue();
    }

    /// <summary>
    /// A release visits only the owner's own resources (#1363 review): it frees what the owner
    /// held and wakes the waiters there, and leaves every other owner's grants and queued requests
    /// as they are.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: ReleaseAll frees only the owner's resources and wakes their waiters")]
    public async Task ReleaseAll_OwnerAmongOthers_ShouldFreeOnlyItsResourcesAndWakeTheirWaiters()
    {
        // Arrange: T1 holds A, T2 holds B, and T3 waits for each of them.
        var locks = LockManager.Create();
        var resourceA = LockResource.Entry(6, 1);
        var resourceB = LockResource.Entry(6, 2);
        await locks.AcquireAsync(_t1, resourceA, LockMode.Exclusive);
        await locks.AcquireAsync(_t2, resourceB, LockMode.Exclusive);
        var waitingForA = locks.AcquireAsync(_t3, resourceA, LockMode.Shared).AsTask();
        var waitingForB = locks.AcquireAsync(_t3, resourceB, LockMode.Shared).AsTask();

        // Act
        locks.ReleaseAll(_t1);
        await waitingForA.WaitAsync(TimeSpan.FromSeconds(5));
        bool bStillWaiting = !waitingForB.IsCompleted;
        bool bStillHeld = !locks.TryAcquire(new TransactionSequence(4), resourceB, LockMode.Shared);
        locks.ReleaseAll(_t2);
        await waitingForB.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t3);

        // Assert: T2's grant and T3's request for B outlived T1's release; once everyone released,
        // both resources are free.
        bStillWaiting.ShouldBeTrue();
        bStillHeld.ShouldBeTrue();
        locks.TryAcquire(new TransactionSequence(4), resourceA, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(new TransactionSequence(4), resourceB, LockMode.Exclusive).ShouldBeTrue();
    }

    /// <summary>
    /// The owner's resources are its grants and its queued requests, whatever became of them: a
    /// canceled wait and a grant made after an earlier release are both released by the owner's
    /// next release (#1363 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: ReleaseAll frees a grant made after an earlier release and survives a canceled wait")]
    public async Task ReleaseAll_AfterACanceledWaitAndALateGrant_ShouldFreeEverything()
    {
        // Arrange: T2 waits for T1's resource and cancels; T2 holds a second resource.
        var locks = LockManager.Create();
        var resourceA = LockResource.Entry(7, 1);
        var resourceB = LockResource.Entry(7, 2);
        await locks.AcquireAsync(_t1, resourceA, LockMode.Exclusive);
        await locks.AcquireAsync(_t2, resourceB, LockMode.Exclusive);
        using var cancellation = new CancellationTokenSource();
        var canceled = locks.AcquireAsync(_t2, resourceA, LockMode.Exclusive, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await canceled.WaitAsync(TimeSpan.FromSeconds(5)));

        // Act: T1 releases, then is granted A again after its release, and releases again; T2
        // releases.
        locks.ReleaseAll(_t1);
        locks.TryAcquire(_t1, resourceA, LockMode.Exclusive).ShouldBeTrue();
        bool aHeldByTheLateGrant = !locks.TryAcquire(_t3, resourceA, LockMode.Shared);
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t2);

        // Assert
        aHeldByTheLateGrant.ShouldBeTrue();
        locks.TryAcquire(_t3, resourceA, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(_t3, resourceB, LockMode.Exclusive).ShouldBeTrue();
    }

    /// <summary>
    /// A waiter whose holder changed while it waited (#1390): the release of the first holder wakes
    /// the request queued ahead of it, so the waiter now waits for a transaction that was not a
    /// holder when it queued. When that transaction then requests a lock the waiter holds, the
    /// detector must see the edge the release created. With edges recorded at the request, the
    /// waiter's only edge named the first holder, which had left, and both transactions waited for
    /// each other until something canceled one of them: the long waits of the #1370 stress runs.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A cycle through a waiter whose holder a release replaced aborts the requester (#1390)")]
    public async Task AcquireAsync_CycleThroughAWaiterWhoseHolderAReleaseReplaced_ShouldAbortTheRequester()
    {
        // Arrange: T1 holds R; T2 queues for R, then T3, which holds S.
        var locks = LockManager.Create();
        var resourceR = LockResource.Entry(8, 1);
        var resourceS = LockResource.Entry(8, 2);
        await locks.AcquireAsync(_t1, resourceR, LockMode.Exclusive);
        await locks.AcquireAsync(_t3, resourceS, LockMode.Exclusive);
        var secondWaits = locks.AcquireAsync(_t2, resourceR, LockMode.Exclusive).AsTask();
        var thirdWaits = locks.AcquireAsync(_t3, resourceR, LockMode.Exclusive).AsTask();

        // Act: T1's release grants R to T2, the request queued first; T3 now waits for T2. T2
        // requesting S, which T3 holds, closes the cycle.
        locks.ReleaseAll(_t1);
        await secondWaits.WaitAsync(TimeSpan.FromSeconds(5));
        bool thirdStillWaiting = !thirdWaits.IsCompleted;
        var closing = locks.AcquireAsync(_t2, resourceS, LockMode.Exclusive).AsTask();
        var victim = await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));

        // The victim aborts and the survivor proceeds.
        locks.ReleaseAll(_t2);
        await thirdWaits.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        thirdStillWaiting.ShouldBeTrue();
        victim.Message.ShouldStartWith($"Transaction {_t2} was chosen as the deadlock victim", Case.Sensitive);
        locks.TryAcquire(_t1, resourceR, LockMode.Shared).ShouldBeFalse();
    }

    /// <summary>
    /// A waiter a compatible request was granted past (#1390): a grant depends on the modes held,
    /// never on the queue, so a shared request is granted beside a shared holder while an exclusive
    /// request waits. Once the first holder leaves, the waiter waits only for the one granted past
    /// it, an edge that did not exist when it queued.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A cycle through a waiter a compatible request was granted past aborts the requester (#1390)")]
    public async Task AcquireAsync_CycleThroughAWaiterACompatibleRequestWasGrantedPast_ShouldAbortTheRequester()
    {
        // Arrange: T1 holds R shared; T3, which holds S, waits for R exclusively; T2 is granted R
        // shared past T3's request.
        var locks = LockManager.Create();
        var resourceR = LockResource.Entry(9, 1);
        var resourceS = LockResource.Entry(9, 2);
        await locks.AcquireAsync(_t1, resourceR, LockMode.Shared);
        await locks.AcquireAsync(_t3, resourceS, LockMode.Exclusive);
        var thirdWaits = locks.AcquireAsync(_t3, resourceR, LockMode.Exclusive).AsTask();
        await locks.AcquireAsync(_t2, resourceR, LockMode.Shared);

        // Act: T1 leaves, so T3 waits for T2 alone; T2 requesting S closes the cycle.
        locks.ReleaseAll(_t1);
        bool thirdStillWaiting = !thirdWaits.IsCompleted;
        var closing = locks.AcquireAsync(_t2, resourceS, LockMode.Exclusive).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        locks.ReleaseAll(_t2);
        await thirdWaits.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        thirdStillWaiting.ShouldBeTrue();
    }

    /// <summary>
    /// A grant that closes a cycle (#1390): an owner with a request queued is granted another
    /// resource that a request of the transaction it waits for is queued on. No request queued at
    /// that moment, so the detector at the request never sees the cycle; the grant must, and the
    /// owner's queued request on the cycle fails as the victim while the grant stands.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A grant that closes a cycle fails the grantee's queued request on it (#1390)")]
    public async Task AcquireAsync_GrantClosingACycle_ShouldFailTheGranteesQueuedRequest()
    {
        // Arrange: T1 waits for P, which T2 holds; T2 waits for R exclusively, which T3 holds shared.
        var locks = LockManager.Create();
        var resourceP = LockResource.Entry(10, 1);
        var resourceR = LockResource.Entry(10, 2);
        await locks.AcquireAsync(_t2, resourceP, LockMode.Exclusive);
        await locks.AcquireAsync(_t3, resourceR, LockMode.Shared);
        var firstWaits = locks.AcquireAsync(_t1, resourceP, LockMode.Exclusive).AsTask();
        var secondWaits = locks.AcquireAsync(_t2, resourceR, LockMode.Exclusive).AsTask();

        // Act: T1 is granted R shared beside T3, so T2 now waits for T1 as T1 waits for T2.
        var granted = locks.AcquireAsync(_t1, resourceR, LockMode.Shared);
        bool grantedAtOnce = granted.IsCompletedSuccessfully;
        await granted;
        var victim = await Should.ThrowAsync<TransactionDeadlockException>(async () => await firstWaits.WaitAsync(TimeSpan.FromSeconds(5)));

        // T1 aborts; T2's wait ends once T3 releases too.
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t3);
        await secondWaits.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        grantedAtOnce.ShouldBeTrue();
        victim.Message.ShouldStartWith($"Transaction {_t1} was chosen as the deadlock victim requesting {LockMode.Exclusive} on {resourceP}", Case.Sensitive);
    }

    /// <summary>
    /// A canceled wait takes only its own edges with it (#1390). An owner may wait for two resources
    /// at once; canceling one wait used to drop every edge the owner had, so a cycle through its
    /// other wait went unseen.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Canceling one of an owner's waits keeps the other's edges (#1390)")]
    public async Task AcquireAsync_OneOfTwoWaitsCanceled_ShouldKeepTheOtherWaitsEdges()
    {
        // Arrange: T1 holds C and waits for A (T2's) and for B (T3's).
        var locks = LockManager.Create();
        var resourceA = LockResource.Entry(11, 1);
        var resourceB = LockResource.Entry(11, 2);
        var resourceC = LockResource.Entry(11, 3);
        await locks.AcquireAsync(_t2, resourceA, LockMode.Exclusive);
        await locks.AcquireAsync(_t3, resourceB, LockMode.Exclusive);
        await locks.AcquireAsync(_t1, resourceC, LockMode.Exclusive);
        var waitsForA = locks.AcquireAsync(_t1, resourceA, LockMode.Exclusive).AsTask();
        using var cancellation = new CancellationTokenSource();
        var waitsForB = locks.AcquireAsync(_t1, resourceB, LockMode.Exclusive, cancellation.Token).AsTask();

        // Act: the wait for B is canceled; T2 requesting C, which T1 holds, closes a cycle through
        // the wait for A.
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await waitsForB.WaitAsync(TimeSpan.FromSeconds(5)));
        var closing = locks.AcquireAsync(_t2, resourceC, LockMode.Exclusive).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        locks.ReleaseAll(_t2);
        await waitsForA.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t3);

        // Assert: nothing is left held or queued.
        locks.TryAcquire(_t2, resourceA, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(_t2, resourceB, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(_t2, resourceC, LockMode.Exclusive).ShouldBeTrue();
    }
}
