using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection(nameof(TransactionEventSourceCollection))]
public sealed class LockStatementMarkTests
{
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
