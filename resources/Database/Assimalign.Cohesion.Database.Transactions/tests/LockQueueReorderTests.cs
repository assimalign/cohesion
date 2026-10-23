using System;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

public sealed class LockQueueReorderTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Locks: a soft cycle reorders the queue and grants a compatible waiter without a victim (#1420)")]
    [InlineData(LockMode.Shared, LockMode.Exclusive, LockMode.Shared)]
    [InlineData(LockMode.IntentShared, LockMode.Exclusive, LockMode.IntentShared)]
    [InlineData(LockMode.IntentExclusive, LockMode.Shared, LockMode.IntentExclusive)]
    [InlineData(LockMode.Update, LockMode.Exclusive, LockMode.Shared)]
    [InlineData(LockMode.Update, LockMode.IntentExclusive, LockMode.Shared)]
    [InlineData(LockMode.IntentExclusive, LockMode.Update, LockMode.IntentExclusive)]
    [InlineData(LockMode.IntentShared, LockMode.Exclusive, LockMode.Update)]
    [InlineData(LockMode.Shared, LockMode.IntentExclusive, LockMode.Update)]
    public async Task AcquireAsync_CycleContainsAReversibleQueueEdge_ShouldReorderWithoutAborting(LockMode held, LockMode blocked, LockMode compatible)
    {
        // Thirty seconds is only a hang guard; completion/grants determine correctness.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var locks = new LockManager();
        var one = new TransactionSequence(1);
        var two = new TransactionSequence(2);
        var three = new TransactionSequence(3);
        var resource = LockResource.Entry(1420, 1);
        var heldByThree = LockResource.Entry(1420, 2);
        await locks.AcquireAsync(one, resource, held, timeout.Token);
        await locks.AcquireAsync(three, heldByThree, LockMode.Exclusive, timeout.Token);
        var second = locks.AcquireAsync(two, resource, blocked, timeout.Token).AsTask();
        var third = locks.AcquireAsync(three, resource, compatible, timeout.Token).AsTask();
        Task? closing = null;
        try
        {
            third.IsCompleted.ShouldBeFalse();
            // 1 -> 3 (held Q), 3 -> 2 (queued R), 2 -> 1 (held R).
            // Moving 3 ahead of 2 permits its S beside 1's S and breaks the cycle.
            closing = locks.AcquireAsync(one, heldByThree, LockMode.Exclusive, timeout.Token).AsTask();
            closing.IsFaulted.ShouldBeFalse();
            await third.WaitAsync(TimeSpan.FromSeconds(30));
            second.IsCompleted.ShouldBeFalse();
            closing.IsCompleted.ShouldBeFalse();
            locks.ReleaseAll(three);
            await closing.WaitAsync(TimeSpan.FromSeconds(30));
            locks.ReleaseAll(one);
            await second.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            locks.ReleaseAll(one); locks.ReleaseAll(two); locks.ReleaseAll(three);
            await Task.WhenAll(closing is null ? [second, third] : [second, third, closing]).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Locks: queue repair considers every request of a multiply waiting owner and leaves hard cycles unchanged (#1420)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_OwnerWaitsOnTwoResources_ShouldRepairOnlyWhenTheHardGraphIsAcyclic(bool hard)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var locks = new LockManager();
        TransactionSequence one = new(1), two = new(2), three = new(3), four = new(4);
        var resource = LockResource.Entry(1420, 3);
        var q = LockResource.Entry(1420, 4);
        var p = LockResource.Entry(1420, 5);
        await locks.AcquireAsync(one, resource, LockMode.Shared, timeout.Token);
        await locks.AcquireAsync(three, q, LockMode.Exclusive, timeout.Token);
        await locks.AcquireAsync(hard ? two : four, p, LockMode.Exclusive, timeout.Token);
        var second = locks.AcquireAsync(two, resource, LockMode.Exclusive, timeout.Token).AsTask();
        var third = locks.AcquireAsync(three, resource, LockMode.Shared, timeout.Token).AsTask();
        var thirdOther = locks.AcquireAsync(three, p, LockMode.Shared, timeout.Token).AsTask();
        Task? closing = null;
        try
        {
            closing = locks.AcquireAsync(one, q, LockMode.Exclusive, timeout.Token).AsTask();
            if (hard)
            {
                await Should.ThrowAsync<TransactionDeadlockException>(async () => await closing);
                third.IsCompleted.ShouldBeFalse(); // failed repair changed no queue
                thirdOther.IsCompleted.ShouldBeFalse();
                locks.ReleaseAll(one);
                await second.WaitAsync(TimeSpan.FromSeconds(30));
                locks.ReleaseAll(two);
                await Task.WhenAll(third, thirdOther).WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                await third.WaitAsync(TimeSpan.FromSeconds(30));
                thirdOther.IsCompleted.ShouldBeFalse();
                closing.IsCompleted.ShouldBeFalse();
                second.IsCompleted.ShouldBeFalse();
                locks.ReleaseAll(four);
                await thirdOther.WaitAsync(TimeSpan.FromSeconds(30));
                locks.ReleaseAll(three);
                await closing.WaitAsync(TimeSpan.FromSeconds(30));
                locks.ReleaseAll(one);
                await second.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            locks.ReleaseAll(one); locks.ReleaseAll(two); locks.ReleaseAll(three); locks.ReleaseAll(four);
            await Task.WhenAll(second, third, thirdOther).WaitAsync(TimeSpan.FromSeconds(30));
            if (!hard && closing is not null) { await closing.WaitAsync(TimeSpan.FromSeconds(30)); }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: cancellation after a reordered grant is dequeued cannot replace that grant (#1420)")]
    public async Task AcquireAsync_CancelAfterReorderedGrantDequeue_ShouldKeepTheGrant()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        var locks = new LockManager();
        TransactionSequence one = new(1), two = new(2), three = new(3);
        var resource = LockResource.Entry(1420, 6);
        var q = LockResource.Entry(1420, 7);
        await locks.AcquireAsync(one, resource, LockMode.Shared, timeout.Token);
        await locks.AcquireAsync(three, q, LockMode.Exclusive, timeout.Token);
        var second = locks.AcquireAsync(two, resource, LockMode.Exclusive, timeout.Token).AsTask();
        var third = locks.AcquireAsync(three, resource, LockMode.Shared, cancellation.Token).AsTask();
        int notifications = 0;
        locks.BeforeGrantNotification = () =>
        {
            locks.BeforeGrantNotification = null;
            third.IsCompleted.ShouldBeFalse();
            cancellation.Cancel();
            notifications++;
        };
        Task? closing = null;
        try
        {
            closing = locks.AcquireAsync(one, q, LockMode.Exclusive, timeout.Token).AsTask();
            await third.WaitAsync(TimeSpan.FromSeconds(30));
            notifications.ShouldBe(1);
            cancellation.IsCancellationRequested.ShouldBeTrue();
            third.IsCompletedSuccessfully.ShouldBeTrue();
            locks.ReleaseAll(three);
            await closing.WaitAsync(TimeSpan.FromSeconds(30));
            locks.ReleaseAll(one);
            await second.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            locks.BeforeGrantNotification = null;
            locks.ReleaseAll(one); locks.ReleaseAll(two); locks.ReleaseAll(three);
            await Task.WhenAll(closing is null ? [second, third] : [second, third, closing]).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Locks: repairing one cycle preserves unrelated conflicting FIFO requests (#1420)")]
    public async Task AcquireAsync_RepairElsewhere_ShouldNotLetAnUnrelatedReaderPassItsQueuedWriter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var locks = new LockManager();
        TransactionSequence one = new(1), two = new(2), three = new(3), five = new(5), six = new(6), seven = new(7);
        var unrelated = LockResource.Object(1420);
        await locks.AcquireAsync(five, unrelated, LockMode.Shared, timeout.Token);
        var writer = locks.AcquireAsync(six, unrelated, LockMode.Exclusive, timeout.Token).AsTask();
        var reader = locks.AcquireAsync(seven, unrelated, LockMode.Shared, timeout.Token).AsTask();
        var r = LockResource.Entry(1420, 8);
        var q = LockResource.Entry(1420, 9);
        await locks.AcquireAsync(one, r, LockMode.Shared, timeout.Token);
        await locks.AcquireAsync(three, q, LockMode.Exclusive, timeout.Token);
        var second = locks.AcquireAsync(two, r, LockMode.Exclusive, timeout.Token).AsTask();
        var third = locks.AcquireAsync(three, r, LockMode.Shared, timeout.Token).AsTask();
        Task? closing = null;
        try
        {
            closing = locks.AcquireAsync(one, q, LockMode.Exclusive, timeout.Token).AsTask();
            await third.WaitAsync(TimeSpan.FromSeconds(30));
            reader.IsCompleted.ShouldBeFalse();
            writer.IsCompleted.ShouldBeFalse();
            locks.ReleaseAll(three);
            await closing.WaitAsync(TimeSpan.FromSeconds(30));
            locks.ReleaseAll(one);
            await second.WaitAsync(TimeSpan.FromSeconds(30));
            locks.ReleaseAll(five);
            await writer.WaitAsync(TimeSpan.FromSeconds(30));
            reader.IsCompleted.ShouldBeFalse();
            locks.ReleaseAll(six);
            await reader.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            foreach (var owner in new[] { one, two, three, five, six, seven }) { locks.ReleaseAll(owner); }
            await Task.WhenAll(closing is null ? [writer, reader, second, third] : [writer, reader, second, third, closing]).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
