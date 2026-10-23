using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Tests for the lock manager: the compatibility matrix and the modes an owner holds, blocking
/// waits with FIFO wake-up and the queue a request waits in, upgrades, cancellation, deadlock
/// victim resolution (#850, #1390), and an ended owner's queued requests (#1225 review).
/// </summary>
public class LockManagerTests
{
    private static readonly TransactionSequence _t1 = new(1);
    private static readonly TransactionSequence _t2 = new(2);
    private static readonly TransactionSequence _t3 = new(3);

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: a queued DDL exclusive request stops a stream of new intent writers (#1163)")]
    public async Task AcquireAsync_ExclusiveBehindIntentWriter_ShouldBlockEveryLaterWriter()
    {
        // Arrange: an admitted writer keeps DDL queued while new writers arrive.
        var locks = LockManager.Create();
        var table = LockResource.Object(1163);
        locks.TryAcquire(_t1, table, LockMode.IntentExclusive).ShouldBeTrue();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ddl = locks.AcquireAsync(_t2, table, LockMode.Exclusive, timeout.Token).AsTask();
        ddl.IsCompleted.ShouldBeFalse();

        // Act: compatibility with the holder must not let even one newcomer pass DDL.
        int refused = 0;
        for (ulong sequence = 3; sequence < 1_003; sequence++)
        {
            var writer = new TransactionSequence(sequence);
            if (!locks.TryAcquire(writer, table, LockMode.IntentExclusive)) { refused++; }
            locks.ReleaseAll(writer);
        }
        refused.ShouldBe(1_000);
        var later = locks.AcquireAsync(_t3, table, LockMode.IntentExclusive, timeout.Token).AsTask();
        later.IsCompleted.ShouldBeFalse();
        locks.ReleaseAll(_t1);
        await ddl.WaitAsync(timeout.Token);

        // Assert: DDL is granted first, and its release admits the queued writer.
        later.IsCompleted.ShouldBeFalse();
        locks.ReleaseAll(_t2);
        await later.WaitAsync(timeout.Token);
        locks.ReleaseAll(_t3);
    }

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
    /// A waiter a holder's grant passed (#1390): an owner that already holds a resource requests
    /// it ahead of the first queued request that waits for it, so a holder of intent-shared is
    /// granted shared past an exclusive request that waits for it and past an intent-exclusive
    /// request behind that, which its intent-shared grant did not block. Once the first holder
    /// leaves, the intent-exclusive request waits for the holder granted past it, an edge that did
    /// not exist when it queued.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A cycle through a waiter a holder's grant passed aborts the requester (#1390)")]
    public async Task AcquireAsync_CycleThroughAWaiterAHoldersGrantPassed_ShouldAbortTheRequester()
    {
        // Arrange: T1 holds R shared and T2 holds it intent-shared; T4 waits for R exclusively,
        // which T2's grant blocks; T3, which holds S, queues behind it for R intent-exclusively,
        // which T1's grant blocks and T2's does not. T2 is granted R shared past both.
        var locks = LockManager.Create();
        var t4 = new TransactionSequence(4);
        var resourceR = LockResource.Entry(9, 1);
        var resourceS = LockResource.Entry(9, 2);
        await locks.AcquireAsync(_t1, resourceR, LockMode.Shared);
        await locks.AcquireAsync(_t2, resourceR, LockMode.IntentShared);
        await locks.AcquireAsync(_t3, resourceS, LockMode.Exclusive);
        var fourthWaits = locks.AcquireAsync(t4, resourceR, LockMode.Exclusive).AsTask();
        var thirdWaits = locks.AcquireAsync(_t3, resourceR, LockMode.IntentExclusive).AsTask();
        var grantedPast = locks.AcquireAsync(_t2, resourceR, LockMode.Shared);
        bool grantedAtOnce = grantedPast.IsCompletedSuccessfully;
        await grantedPast.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Act: T1 leaves, so T3 waits for T2 and the request ahead of it; T2 requesting S closes
        // T2 -> T3 -> T2.
        locks.ReleaseAll(_t1);
        bool thirdStillWaiting = !thirdWaits.IsCompleted;
        var closing = locks.AcquireAsync(_t2, resourceS, LockMode.Exclusive).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        locks.ReleaseAll(_t2);
        await fourthWaits.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(t4);
        await thirdWaits.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t3);

        // Assert
        grantedAtOnce.ShouldBeTrue();
        thirdStillWaiting.ShouldBeTrue();
    }

    /// <summary>
    /// A request the old grant rule passed a queued request with, closing a cycle, now queues
    /// behind it and closes none (#1390 review). T1 was granted R shared beside T3 and past T2's
    /// exclusive request, so T2 waited for T1 as T1 waited for T2, and the grant had to fail T1's
    /// queued request as the deadlock victim. Behind T2's request, T1 waits for T2 on R too, which
    /// is no cycle: T2 waits only for T3, and everyone is granted once T3 and T2 end.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A request queued behind a conflicting request closes no cycle a grant past it would have (#1390)")]
    public async Task AcquireAsync_RequestAGrantPastAQueuedRequestWouldHaveDeadlocked_ShouldQueueWithoutADeadlock()
    {
        // Arrange: T1 waits for P, which T2 holds; T2 waits for R exclusively, which T3 holds shared.
        var locks = LockManager.Create();
        var resourceP = LockResource.Entry(10, 1);
        var resourceR = LockResource.Entry(10, 2);
        await locks.AcquireAsync(_t2, resourceP, LockMode.Exclusive);
        await locks.AcquireAsync(_t3, resourceR, LockMode.Shared);
        var firstWaits = locks.AcquireAsync(_t1, resourceP, LockMode.Exclusive).AsTask();
        var secondWaits = locks.AcquireAsync(_t2, resourceR, LockMode.Exclusive).AsTask();

        // Act: T1 requests R shared, which T3's grant allows and T2's queued request does not.
        var shared = locks.AcquireAsync(_t1, resourceR, LockMode.Shared).AsTask();
        bool sharedQueued = !shared.IsCompleted;
        bool firstStillWaiting = !firstWaits.IsCompleted;
        locks.ReleaseAll(_t3);
        await secondWaits.WaitAsync(TimeSpan.FromSeconds(5));
        bool sharedWaitsForTheExclusiveGrant = !shared.IsCompleted;
        locks.ReleaseAll(_t2);
        await firstWaits.WaitAsync(TimeSpan.FromSeconds(5));
        await shared.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t1);

        // Assert: no request failed, and each was granted in turn.
        sharedQueued.ShouldBeTrue();
        firstStillWaiting.ShouldBeTrue();
        sharedWaitsForTheExclusiveGrant.ShouldBeTrue();
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

    /// <summary>
    /// A request queues behind a conflicting request of another owner queued ahead of it (#1390
    /// review). A grant used to depend on the modes held only, so a stream of shared grants passed
    /// an exclusive request for as long as the stream lasted: under a stress workload concentrated
    /// on one parent row, a cascading delete's exclusive request on that row waited seven times in
    /// a row for 12 s, until the watchdog canceled it, while the child writers' shared requests
    /// were granted past it. PostgreSQL's lock manager queues such a request
    /// (<c>src/backend/storage/lmgr/lock.c:1136-1143</c>).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A request queues behind a conflicting request queued ahead of it (#1390)")]
    public async Task AcquireAsync_SharedRequestBehindAQueuedExclusiveRequest_ShouldQueueBehindIt()
    {
        // Arrange: T1 holds R shared; T2 waits for R exclusively.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(12, 1);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        var exclusive = locks.AcquireAsync(_t2, resource, LockMode.Exclusive).AsTask();

        // Act: T3 requests R shared, which T1's grant allows and T2's queued request does not.
        var shared = locks.AcquireAsync(_t3, resource, LockMode.Shared).AsTask();
        bool sharedQueued = !shared.IsCompleted;
        bool tryRefused = !locks.TryAcquire(new TransactionSequence(4), resource, LockMode.Shared);
        locks.ReleaseAll(_t1);
        await exclusive.WaitAsync(TimeSpan.FromSeconds(5));
        bool sharedWaitsForTheExclusiveGrant = !shared.IsCompleted;
        locks.ReleaseAll(_t2);
        await shared.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t3);

        // Assert: the shared request waited for the exclusive one, which the first release granted.
        sharedQueued.ShouldBeTrue();
        tryRefused.ShouldBeTrue();
        sharedWaitsForTheExclusiveGrant.ShouldBeTrue();
    }

    /// <summary>
    /// An owner that already holds a resource requests it ahead of the queued requests that wait
    /// for it (#1390 review): they cannot be granted before it ends anyway, and a statement apply
    /// re-requests, inside the coordinator's gate, locks its statement already holds, which must be
    /// granted at once.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A holder's request goes ahead of a queued request that waits for it (#1390)")]
    public async Task AcquireAsync_HoldersRequestBehindAQueuedRequestThatWaitsForIt_ShouldBeGrantedAtOnce()
    {
        // Arrange: T1 holds R shared; T2 waits for R exclusively.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(13, 1);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        var exclusive = locks.AcquireAsync(_t2, resource, LockMode.Exclusive).AsTask();

        // Act: T1 re-requests its mode, then upgrades.
        var regrant = locks.AcquireAsync(_t1, resource, LockMode.Shared);
        bool regrantedAtOnce = regrant.IsCompletedSuccessfully;
        await regrant;
        var upgrade = locks.AcquireAsync(_t1, resource, LockMode.Exclusive);
        bool upgradedAtOnce = upgrade.IsCompletedSuccessfully;
        await upgrade;
        bool exclusiveStillWaiting = !exclusive.IsCompleted;
        locks.ReleaseAll(_t1);
        await exclusive.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t2);

        // Assert
        regrantedAtOnce.ShouldBeTrue();
        upgradedAtOnce.ShouldBeTrue();
        exclusiveStillWaiting.ShouldBeTrue();
    }

    /// <summary>
    /// A holder's request queues behind a conflicting queued request that does not wait for it
    /// (#1390 review). Otherwise owner after owner could join the holders in a mode the queued
    /// request allows, intent-shared beside a waiting intent-exclusive, then upgrade past it to
    /// one it does not, and keep it waiting for as long as they came.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A holder's request queues behind a queued request that does not wait for it (#1390)")]
    public async Task AcquireAsync_HoldersUpgradeBehindAQueuedRequestThatDoesNotWaitForIt_ShouldQueueBehindIt()
    {
        // Arrange: T1 holds R shared; T2 waits for R intent-exclusively; T3 is granted R
        // intent-shared, which both allow.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(19, 1);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        var intentExclusive = locks.AcquireAsync(_t2, resource, LockMode.IntentExclusive).AsTask();
        var intentShared = locks.AcquireAsync(_t3, resource, LockMode.IntentShared);
        bool intentSharedAtOnce = intentShared.IsCompletedSuccessfully;
        await intentShared.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Act: T3 requests shared, which T1's grant allows and T2's queued request does not.
        bool tryRefused = !locks.TryAcquire(_t3, resource, LockMode.Shared);
        var upgrade = locks.AcquireAsync(_t3, resource, LockMode.Shared).AsTask();
        bool upgradeQueued = !upgrade.IsCompleted;
        locks.ReleaseAll(_t1);
        await intentExclusive.WaitAsync(TimeSpan.FromSeconds(5));
        bool upgradeWaitsForTheIntentExclusiveGrant = !upgrade.IsCompleted;
        locks.ReleaseAll(_t2);
        await upgrade.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t3);

        // Assert
        intentSharedAtOnce.ShouldBeTrue();
        tryRefused.ShouldBeTrue();
        upgradeQueued.ShouldBeTrue();
        upgradeWaitsForTheIntentExclusiveGrant.ShouldBeTrue();
    }

    /// <summary>
    /// A cycle with a queued edge can be resolved without a victim (#1420): queue the
    /// compatible shared request ahead of the exclusive request, then retain every
    /// real held-lock dependency until its owner releases.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A soft cycle reorders the queue while held-lock blockers remain (#1420)")]
    public async Task AcquireAsync_CycleThroughARequestQueuedBehindAnother_ShouldReorderTheCompatibleWaiter()
    {
        var locks = LockManager.Create();
        var resourceR = LockResource.Entry(14, 1);
        var resourceP = LockResource.Entry(14, 2);
        var resourceQ = LockResource.Entry(14, 3);
        await locks.AcquireAsync(_t1, resourceR, LockMode.Shared);
        await locks.AcquireAsync(_t2, resourceP, LockMode.Exclusive);
        await locks.AcquireAsync(_t3, resourceQ, LockMode.Exclusive);
        var secondWaits = locks.AcquireAsync(_t2, resourceR, LockMode.Exclusive).AsTask();
        var thirdWaits = locks.AcquireAsync(_t3, resourceR, LockMode.Shared).AsTask();
        bool thirdQueued = !thirdWaits.IsCompleted;
        Task? closing = null;
        try
        {
            closing = locks.AcquireAsync(_t1, resourceQ, LockMode.Exclusive).AsTask();
            // Thirty seconds is only a hang guard; grants decide each assertion.
            await thirdWaits.WaitAsync(TimeSpan.FromSeconds(30));
            thirdQueued.ShouldBeTrue();
            closing.IsCompleted.ShouldBeFalse();
            secondWaits.IsCompleted.ShouldBeFalse();
            locks.ReleaseAll(_t3);
            await closing.WaitAsync(TimeSpan.FromSeconds(30));
            secondWaits.IsCompleted.ShouldBeFalse();
            locks.ReleaseAll(_t1);
            await secondWaits.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            locks.ReleaseAll(_t1); locks.ReleaseAll(_t2); locks.ReleaseAll(_t3);
            await Task.WhenAll(closing is null ? [secondWaits, thirdWaits] : [secondWaits, thirdWaits, closing]).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>
    /// A request that leaves the queue without a grant lets the requests behind it be granted
    /// (#1390 review): once a request waits for the requests queued ahead of it, a canceled one
    /// is a wake-up like a release, or the request behind it waits for a grant that never comes.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A canceled request grants the request queued behind it (#1390)")]
    public async Task AcquireAsync_QueuedRequestAheadCanceled_ShouldGrantTheRequestBehindIt()
    {
        // Arrange: T1 holds R shared; T2 waits for R exclusively; T3 queues for R shared behind it.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(15, 1);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        using var cancellation = new CancellationTokenSource();
        var exclusive = locks.AcquireAsync(_t2, resource, LockMode.Exclusive, cancellation.Token).AsTask();
        var shared = locks.AcquireAsync(_t3, resource, LockMode.Shared).AsTask();
        bool sharedQueued = !shared.IsCompleted;

        // Act: T2's wait is canceled; nothing is released.
        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await exclusive.WaitAsync(TimeSpan.FromSeconds(5)));
        await shared.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t3);

        // Assert
        sharedQueued.ShouldBeTrue();
    }

    /// <summary>
    /// Ending an owner while its request waits lets the requests queued behind that request be
    /// granted (#1390 review), as a canceled request does.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: Ending an owner grants the request queued behind its request (#1390)")]
    public async Task ReleaseAll_OwnerWithARequestQueuedAheadOfAnother_ShouldGrantTheRequestBehindIt()
    {
        // Arrange: T1 holds R shared; T2 waits for R exclusively; T3 queues for R shared behind it.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(16, 1);
        await locks.AcquireAsync(_t1, resource, LockMode.Shared);
        var exclusive = locks.AcquireAsync(_t2, resource, LockMode.Exclusive).AsTask();
        var shared = locks.AcquireAsync(_t3, resource, LockMode.Shared).AsTask();
        bool sharedQueued = !shared.IsCompleted;

        // Act: T2 ends while it waits.
        locks.ReleaseAll(_t2);
        await Should.ThrowAsync<TransactionAbortedException>(async () => await exclusive.WaitAsync(TimeSpan.FromSeconds(5)));
        await shared.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t3);

        // Assert
        sharedQueued.ShouldBeTrue();
    }

    /// <summary>
    /// An owner keeps every mode it was granted (#1390 review). The table kept only the strongest by
    /// a fixed order (IS, S, IX, U, X), which the five modes do not have: intent-exclusive then
    /// update kept update, which allows shared, and lost intent-exclusive's conflict with it. A
    /// request queued for shared before the update grant still waited, since nothing woke it, but
    /// the detector, which reads its edges from the table, found none, and a cycle through it went
    /// unseen. PostgreSQL keeps a mask of the modes each holder holds
    /// (<c>src/backend/storage/lmgr/lock.c:1702-1709</c>, <c>holdMask</c>).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A cycle through a waiter whose holder was granted update after intent-exclusive aborts the requester (#1390)")]
    public async Task AcquireAsync_CycleThroughAHolderOfIntentExclusiveAndUpdate_ShouldAbortTheRequester()
    {
        // Arrange: T3 holds R intent-exclusive; T1 holds Q shared and queues for R shared; T3 is
        // granted R update as well.
        var locks = LockManager.Create();
        var resourceR = LockResource.Entry(17, 1);
        var resourceQ = LockResource.Entry(17, 2);
        await locks.AcquireAsync(_t3, resourceR, LockMode.IntentExclusive);
        await locks.AcquireAsync(_t1, resourceQ, LockMode.Shared);
        var firstWaits = locks.AcquireAsync(_t1, resourceR, LockMode.Shared).AsTask();
        bool firstQueued = !firstWaits.IsCompleted;
        await locks.AcquireAsync(_t3, resourceR, LockMode.Update).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // Act: T3 requesting Q closes T3 -> T1 -> T3.
        var closing = locks.AcquireAsync(_t3, resourceQ, LockMode.Exclusive).AsTask();
        var victim = await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing.WaitAsync(TimeSpan.FromSeconds(5)));
        locks.ReleaseAll(_t3);
        await firstWaits.WaitAsync(TimeSpan.FromSeconds(5));
        locks.ReleaseAll(_t1);

        // Assert
        firstQueued.ShouldBeTrue();
        victim.Message.ShouldStartWith($"Transaction {_t3} was chosen as the deadlock victim requesting {LockMode.Exclusive} on {resourceQ}", Case.Sensitive);
    }

    /// <summary>
    /// Two owners never hold conflicting modes (#1390 review): an owner granted intent-exclusive
    /// and then shared kept only intent-exclusive, the stronger by the old order, so another
    /// owner's intent-exclusive, which shared conflicts with, was granted beside it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: A holder of intent-exclusive and shared blocks another owner's intent-exclusive (#1390)")]
    public async Task TryAcquire_HolderOfIntentExclusiveAndShared_ShouldRefuseAnotherOwnersIntentExclusive()
    {
        // Arrange: T3 holds R intent-exclusive and shared.
        var locks = LockManager.Create();
        var resource = LockResource.Entry(18, 1);
        await locks.AcquireAsync(_t3, resource, LockMode.IntentExclusive);
        await locks.AcquireAsync(_t3, resource, LockMode.Shared);

        // Act
        bool intentExclusive = locks.TryAcquire(_t1, resource, LockMode.IntentExclusive);
        bool shared = locks.TryAcquire(_t1, resource, LockMode.Shared);
        bool intentShared = locks.TryAcquire(_t1, resource, LockMode.IntentShared);
        locks.ReleaseAll(_t1);
        locks.ReleaseAll(_t3);

        // Assert: intent-shared is the one mode compatible with both.
        intentExclusive.ShouldBeFalse();
        shared.ShouldBeFalse();
        intentShared.ShouldBeTrue();
    }
}
