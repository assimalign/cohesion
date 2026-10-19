using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection(nameof(TransactionEventSourceCollection))]
public sealed class LockStatementMarkTests
{
    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: value equality includes manager, owner and grant boundary")]
    public void Equals_SameBoundary_ShouldHaveValueSemantics()
    {
        // Arrange
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1);
        var first = locks.MarkStatement(owner);
        IEquatable<LockStatementMark> typed = first;
        var same = locks.MarkStatement(owner);
        var otherOwner = locks.MarkStatement(new TransactionSequence(2));
        var otherManager = LockManager.Create().MarkStatement(owner);
        locks.TryAcquire(owner, LockResource.Entry(1, 1), LockMode.Exclusive).ShouldBeTrue();
        var later = locks.MarkStatement(owner);

        // Act / Assert
        typed.Equals(same).ShouldBeTrue();
        first.Equals((object)same).ShouldBeTrue();
        first.Equals(null).ShouldBeFalse();
        first.Equals((object)owner).ShouldBeFalse();
        (first == same).ShouldBeTrue();
        (first != same).ShouldBeFalse();
        first.GetHashCode().ShouldBe(same.GetHashCode());
        first.Equals(otherOwner).ShouldBeFalse();
        first.Equals(otherManager).ShouldBeFalse();
        (first != later).ShouldBeTrue();
        (default(LockStatementMark) == default).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: keeping waited rows ends the symmetric hand-off and preserves earlier waits")]
    public async Task ReleaseSince_RetainedWaitGrants_ShouldMakeArbitrationProgress()
    {
        // Arrange: the reviewer's production-lock-manager probe, with a new mark after each wait.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var elapsed = Stopwatch.StartNew();
        int arbitrations = 0;
        int waitsCompleted = 0;
        do
        {
            var locks = LockManager.Create();
            var first = new TransactionSequence(1);
            var second = new TransactionSequence(2);
            var holder = new TransactionSequence(3);
            var row = LockResource.Entry(1, 1);
            var nextRow = LockResource.Entry(1, 2);
            var key = LockResource.Entry(1, 3);
            var firstMark = locks.MarkStatement(first);
            var secondMark = locks.MarkStatement(second);
            locks.TryAcquire(holder, row, LockMode.Exclusive).ShouldBeTrue();
            locks.TryAcquire(holder, nextRow, LockMode.Exclusive).ShouldBeTrue();
            using var waits = new LockWaitListener();
            Task firstWait = BackOffAsync(first, firstMark);
            await waits.WaitForWaitAsync(first, timeout.Token);
            Task secondWait = BackOffAsync(second, secondMark);
            await waits.WaitForWaitAsync(second, timeout.Token);
            try
            {
                // Act: restart the first waiter with its row held before its key.
                locks.ReleaseAll(holder);
                await firstWait.WaitAsync(timeout.Token);
                waitsCompleted++;
                firstMark = locks.MarkStatement(first);
                locks.TryAcquire(first, key, LockMode.Exclusive).ShouldBeTrue();
                locks.TryAcquire(first, row, LockMode.Exclusive).ShouldBeTrue();
                arbitrations++;
                secondWait.IsCompleted.ShouldBeFalse();

                // A later back-off preserves the earlier waited row and keeps the next one too.
                locks.TryAcquire(holder, nextRow, LockMode.Exclusive).ShouldBeTrue();
                locks.TryAcquire(first, nextRow, LockMode.Exclusive).ShouldBeFalse();
                locks.ReleaseSince(firstMark);
                locks.TryAcquire(holder, row, LockMode.Exclusive).ShouldBeFalse();
                var nextWait = locks.AcquireAsync(first, nextRow, LockMode.Exclusive, timeout.Token).AsTask();
                locks.ReleaseAll(holder);
                await nextWait.WaitAsync(timeout.Token);
                waitsCompleted++;
                firstMark = locks.MarkStatement(first);
                locks.ReleaseSince(firstMark);
                locks.TryAcquire(holder, row, LockMode.Exclusive).ShouldBeFalse();
                locks.TryAcquire(holder, nextRow, LockMode.Exclusive).ShouldBeFalse();
                locks.ReleaseAll(first);
                await secondWait.WaitAsync(timeout.Token);
                waitsCompleted++;
                secondMark = locks.MarkStatement(second);
                locks.ReleaseSince(secondMark);
                locks.TryAcquire(second, key, LockMode.Exclusive).ShouldBeTrue();
                locks.TryAcquire(second, row, LockMode.Exclusive).ShouldBeTrue();
                arbitrations++;
            }
            finally
            {
                locks.ReleaseAll(first);
                locks.ReleaseAll(second);
                locks.ReleaseAll(holder);
            }

            Task BackOffAsync(TransactionSequence owner, LockStatementMark mark)
            {
                locks.TryAcquire(owner, key, LockMode.Exclusive).ShouldBeTrue();
                locks.TryAcquire(owner, row, LockMode.Exclusive).ShouldBeFalse();
                locks.ReleaseSince(mark);
                return locks.AcquireAsync(owner, row, LockMode.Exclusive, timeout.Token).AsTask();
            }
        }
        while (elapsed.Elapsed < TimeSpan.FromMilliseconds(250) || arbitrations < 6);

        // Assert: each pair completed both arbitrations, without a hand-off loop.
        arbitrations.ShouldBeGreaterThanOrEqualTo(6);
        waitsCompleted.ShouldBe(arbitrations / 2 * 3);
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: preserve earlier grants, re-grants and upgrades")]
    public void ReleaseSince_PreviousGrants_ShouldKeepEveryMode()
    {
        // Arrange
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1);
        var other = new TransactionSequence(2);
        var earlier = LockResource.Entry(1, 1);
        var newer = LockResource.Entry(1, 2);
        locks.TryAcquire(owner, earlier, LockMode.Shared).ShouldBeTrue();
        var mark = locks.MarkStatement(owner);

        // Act
        locks.TryAcquire(owner, earlier, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(owner, newer, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(owner, newer, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseSince(mark);

        // Assert: the existing resource keeps its upgrade; the re-granted new resource leaves.
        locks.TryAcquire(other, earlier, LockMode.Shared).ShouldBeFalse();
        locks.TryAcquire(other, newer, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(other);
        locks.TryAcquire(owner, newer, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseSince(mark);
        locks.TryAcquire(other, newer, LockMode.Exclusive).ShouldBeTrue();
        locks.TryAcquire(other, earlier, LockMode.Shared).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: release wakes the FIFO queue without ending the owner")]
    public async Task ReleaseSince_QueuedRequests_ShouldWakeInOrder()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1);
        var second = new TransactionSequence(2);
        var third = new TransactionSequence(3);
        var resource = LockResource.Entry(1, 1);
        var mark = locks.MarkStatement(owner);
        locks.TryAcquire(owner, resource, LockMode.Exclusive).ShouldBeTrue();
        using var waits = new LockWaitListener();
        var first = locks.AcquireAsync(second, resource, LockMode.Exclusive, timeout.Token).AsTask();
        await waits.WaitForWaitAsync(second, timeout.Token);
        var next = locks.AcquireAsync(third, resource, LockMode.Shared, timeout.Token).AsTask();
        await waits.WaitForWaitAsync(third, timeout.Token);

        // Act
        locks.ReleaseSince(mark);
        await first.WaitAsync(timeout.Token);

        // Assert
        next.IsCompleted.ShouldBeFalse();
        locks.ReleaseAll(second);
        await next.WaitAsync(timeout.Token);
        locks.ReleaseAll(third);
        locks.TryAcquire(owner, resource, LockMode.Exclusive).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: abandoned writers retain every grant for deferred undo")]
    public void ReleaseSince_AbandonedOwner_ShouldKeepGrants()
    {
        // Arrange
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1);
        var resource = LockResource.Entry(1, 1);
        var mark = locks.MarkStatement(owner);
        locks.TryAcquire(owner, resource, LockMode.Exclusive).ShouldBeTrue();
        locks.AbandonPending(owner);

        // Act
        locks.ReleaseSince(mark);

        // Assert
        locks.TryAcquire(new TransactionSequence(2), resource, LockMode.Exclusive).ShouldBeFalse();
        locks.ReleaseAll(owner);
        locks.TryAcquire(new TransactionSequence(2), resource, LockMode.Exclusive).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: default and foreign marks are refused")]
    public void ReleaseSince_InvalidMark_ShouldReject()
    {
        // Arrange
        var locks = LockManager.Create();
        var foreign = LockManager.Create().MarkStatement(new TransactionSequence(1));

        // Act / Assert
        Should.Throw<ArgumentException>(() => locks.ReleaseSince(default));
        Should.Throw<ArgumentException>(() => locks.ReleaseSince(foreign));
    }

    [Fact(DisplayName = "Cohesion Test [Transactions] - Statement marks: mark and successful re-grant allocate nothing")]
    public void MarkStatement_UncontendedRegrant_ShouldAllocateNothing()
    {
        // Arrange
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1);
        var resource = LockResource.Entry(1, 1);
        locks.TryAcquire(owner, resource, LockMode.Exclusive).ShouldBeTrue();
        for (int i = 0; i < 100; i++) { _ = locks.MarkStatement(owner); locks.TryAcquire(owner, resource, LockMode.Exclusive); }

        // Act
        long start = GC.GetAllocatedBytesForCurrentThread();
        int granted = 0;
        for (int i = 0; i < 1000; i++)
        {
            _ = locks.MarkStatement(owner);
            if (locks.TryAcquire(owner, resource, LockMode.Exclusive)) { granted++; }
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        // Assert
        granted.ShouldBe(1000);
        allocated.ShouldBe(0);
    }
}
