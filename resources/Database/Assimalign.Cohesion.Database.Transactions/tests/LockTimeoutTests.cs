using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

[Collection(nameof(TransactionEventSourceCollection))]
public sealed class LockTimeoutTests
{
    private static readonly TransactionSequence _holder = new(1);
    private static readonly TransactionSequence _waiter = new(2);
    private static readonly TransactionSequence _probe = new(3);
    private static readonly LockResource _row = LockResource.Entry(7, 7);

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: a queued request times out, reports Timeout and cannot receive a late grant")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_Timeout_ShouldRemoveWaitAndReportOutcome(bool engineMode)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var locks = Create(engineMode);
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, System.Diagnostics.Tracing.EventLevel.Verbose);
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();

        // Act
        using (locks.EnterLockTimeout(TimeSpan.FromMilliseconds(30)))
        {
            await Should.ThrowAsync<TransactionLockTimeoutException>(async () =>
                await locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token));
        }
        locks.ReleaseAll(_holder);

        // Assert: no stranded queued request takes the released lock.
        locks.TryAcquire(_probe, _row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(_probe);
        var stopped = recorder.Events.Where(e => e.EventName == "LockWaitStop").ShouldHaveSingleItem();
        stopped.Payload![2].ShouldBe("Timeout");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: each wait gets a new timer and scopes restore after disposal and exceptions")]
    public async Task AcquireAsync_SeparateWaitsAndNestedScopes_ShouldResetAndRestoreTimeout()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var locks = Create(true);
        using var outer = locks.EnterLockTimeout(TimeSpan.FromMilliseconds(500));

        // Act: total elapsed time exceeds one timeout, but neither wait does.
        var elapsed = Stopwatch.StartNew();
        for (int index = 0; index < 2; index++)
        {
            locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
            var pending = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
            try
            {
                await Task.Delay(300, ceiling.Token);
                pending.IsCompleted.ShouldBeFalse();
            }
            finally { locks.ReleaseAll(_holder); }
            await pending.WaitAsync(ceiling.Token);
            locks.ReleaseAll(_waiter);
        }
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() =>
        {
            using var inner = locks.EnterLockTimeout(TimeSpan.Zero);
            throw new InvalidOperationException();
        });
        await Should.ThrowAsync<TransactionLockTimeoutException>(async () =>
            await locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token));
        outer.Dispose();
        var untimed = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        try
        {
            await Task.Delay(600, ceiling.Token);
            untimed.IsCompleted.ShouldBeFalse();
        }
        finally { locks.ReleaseAll(_holder); }
        await untimed.WaitAsync(ceiling.Token);

        // Assert
        elapsed.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500));
        locks.ReleaseAll(_waiter);
        locks.TryAcquire(_probe, _row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(_probe);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: concurrent flows do not inherit each other's scopes")]
    public async Task AcquireAsync_ConcurrentScopes_ShouldRemainIndependent()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var locks = Create(true);
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timed = Task.Run(async () =>
        {
            using var scope = locks.EnterLockTimeout(TimeSpan.FromMilliseconds(30));
            entered.SetResult();
            await Should.ThrowAsync<TransactionLockTimeoutException>(async () =>
                await locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token));
        }, ceiling.Token);
        await entered.Task.WaitAsync(ceiling.Token);

        // Act: this flow has no scope, even while its neighbor's timer expires.
        var untimed = locks.AcquireAsync(_probe, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        try { await timed; untimed.IsCompleted.ShouldBeFalse(); }
        finally { locks.ReleaseAll(_holder); }
        await untimed.WaitAsync(ceiling.Token);

        // Assert
        locks.ReleaseAll(_probe);
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(_holder);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: caller cancellation, owner end, deadlock and apply refusal retain their own meanings")]
    public async Task AcquireAsync_OtherEndReasons_ShouldNotBecomeTimeout()
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var locks = Create(true);
        using var scope = locks.EnterLockTimeout(TimeSpan.FromSeconds(5));
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);

        // Act / Assert
        var canceled = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, caller.Token).AsTask();
        caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => canceled);
        var ended = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        locks.ReleaseAll(_waiter);
        await Should.ThrowAsync<TransactionAbortedException>(() => ended);
        var other = LockResource.Entry(7, 8);
        locks.TryAcquire(_waiter, other, LockMode.Exclusive).ShouldBeTrue();
        var waiting = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(async () =>
            await locks.AcquireAsync(_holder, other, LockMode.Exclusive, ceiling.Token));
        locks.ReleaseAll(_holder);
        await waiting.WaitAsync(ceiling.Token);
        locks.ReleaseAll(_waiter);
        var applying = LockManager.Create();
        applying.EnterEngineMode(_ => false, () => true, "apply-refusal");
        using var applyTimeout = applying.EnterLockTimeout(TimeSpan.FromMilliseconds(1));
        applying.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await applying.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token));
        applying.ReleaseAll(_holder);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: grant, timer and caller cancellation races leave no hidden grant or waiter")]
    public async Task AcquireAsync_GrantCancellationRace_ShouldHaveOneOutcomeAndReleaseEverything()
    {
        // Arrange: require all outcomes and exact wait counters, with a minimum duration and
        // a ceiling. Different schedules force both timeout/cancellation and concurrent grants.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var locks = Create(true);
        int granted = 0, timedOut = 0, canceled = 0, iterations = 0;
        long waits = TransactionEventSource.Log.TotalLockWaits;
        var elapsed = Stopwatch.StartNew();

        // Act
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || granted < 8 || timedOut < 8 || canceled < 8)
        {
            ceiling.Token.ThrowIfCancellationRequested();
            locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
            using var scope = locks.EnterLockTimeout(TimeSpan.FromMilliseconds(2));
            var pending = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, caller.Token).AsTask();
            Task release = Task.CompletedTask;
            try
            {
                switch (iterations % 3)
                {
                    case 0: release = Task.Run(() => locks.ReleaseAll(_holder), ceiling.Token); break;
                    case 1: break;
                    case 2: caller.Cancel(); release = Task.Run(() => locks.ReleaseAll(_holder), ceiling.Token); break;
                }
                try { await pending.WaitAsync(ceiling.Token); granted++; }
                catch (TransactionLockTimeoutException) { timedOut++; }
                catch (OperationCanceledException) when (!ceiling.IsCancellationRequested) { canceled++; }
            }
            finally
            {
                await release;
                locks.ReleaseAll(_holder);
                // A canceled request must not have secretly won a concurrent grant.
                if (pending.IsCompletedSuccessfully) { locks.ReleaseAll(_waiter); }
            }
            locks.TryAcquire(_probe, _row, LockMode.Exclusive).ShouldBeTrue();
            locks.ReleaseAll(_probe);
            iterations++;
        }

        // Assert
        granted.ShouldBeGreaterThanOrEqualTo(8);
        timedOut.ShouldBeGreaterThanOrEqualTo(8);
        canceled.ShouldBeGreaterThanOrEqualTo(8);
        (granted + timedOut + canceled).ShouldBe(iterations);
        (TransactionEventSource.Log.TotalLockWaits - waits).ShouldBe(iterations);
    }

    private static LockManager Create(bool engineMode)
    {
        var locks = LockManager.Create();
        if (engineMode) { locks.EnterEngineMode(_ => false, () => false, "timeout"); }
        return locks;
    }
}
