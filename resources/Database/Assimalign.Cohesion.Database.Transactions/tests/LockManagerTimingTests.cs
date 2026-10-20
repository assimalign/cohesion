using System;
using System.Diagnostics;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Transactions.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>Guards owner-local release against a return to scanning the shared table (#1250).</summary>
[Collection(LockTimingCollection.Name)]
public sealed class LockManagerTimingTests
{
    private readonly ITestOutputHelper _output;

    public LockManagerTimingTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - ReleaseAll: ten owned locks cost the same beside 1,000 and 100,000 foreign locks (#1250)")]
    public void ReleaseAll_ManyForeignLocks_ShouldHaveOwnerLocalCost()
    {
        // Arrange: identical owner workloads, with 100 times as many unrelated live entries.
        var small = CreateTable(1_000);
        var large = CreateTable(100_000);
        Measure(small);
        Measure(large);

        // Act: alternate sizes, taking the minimum of five rounds to exclude scheduling/GC noise.
        double smallTicks = double.MaxValue;
        double largeTicks = double.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            smallTicks = Math.Min(smallTicks, Measure(small));
            largeTicks = Math.Min(largeTicks, Measure(large));
        }

        // Assert: a whole-table scan grows about 100-fold; allow eight for host noise.
        double ratio = largeTicks / smallTicks;
        _output.WriteLine($"ReleaseAll(10): 1,000 foreign = {smallTicks * 1_000_000 / Stopwatch.Frequency:F3} us; "
            + $"100,000 foreign = {largeTicks * 1_000_000 / Stopwatch.Frequency:F3} us; ratio {ratio:F3}");
        ratio.ShouldBeLessThan(8.0);
        VerifyForeignGrant(small);
        VerifyForeignGrant(large);
    }

    private static LockManager CreateTable(int count)
    {
        var locks = LockManager.Create();
        for (ulong index = 0; index < (ulong)count; index++)
        {
            locks.TryAcquire(new TransactionSequence(1), LockResource.Entry(1, index), LockMode.Exclusive).ShouldBeTrue();
        }
        return locks;
    }

    private static double Measure(LockManager locks)
    {
        const int operations = 300;
        var owner = new TransactionSequence(2);
        long ticks = 0;
        for (int operation = 0; operation < operations; operation++)
        {
            for (ulong index = 0; index < 10; index++)
            {
                locks.TryAcquire(owner, LockResource.Entry(2, index), LockMode.Exclusive).ShouldBeTrue();
            }
            long started = Stopwatch.GetTimestamp();
            locks.ReleaseAll(owner);
            ticks += Stopwatch.GetTimestamp() - started;
        }
        locks.TryAcquire(new TransactionSequence(3), LockResource.Entry(2, 0), LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(new TransactionSequence(3));
        return (double)ticks / operations;
    }

    private static void VerifyForeignGrant(LockManager locks)
    {
        locks.TryAcquire(new TransactionSequence(3), LockResource.Entry(1, 0), LockMode.Exclusive).ShouldBeFalse();
        locks.ReleaseAll(new TransactionSequence(3));
        locks.ReleaseAll(new TransactionSequence(1));
    }
}
