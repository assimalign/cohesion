using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions.Internal;
using Assimalign.Cohesion.Testing;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

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
        using var outer = locks.EnterLockTimeout(TimeSpan.FromHours(1));
        CancellationTokenSource? timer = null;
        locks.WaitTimerArmed = source => { source.CancelAfter(Timeout.InfiniteTimeSpan); timer = source; };
        CancellationTokenSource? previous = null;

        // Act: each queued wait owns a fresh timer, independent of the preceding wait.
        for (int index = 0; index < 2; index++)
        {
            locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
            var pending = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
            try
            {
                timer.ShouldNotBeNull().ShouldNotBeSameAs(previous);
                previous = timer;
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
        var timed = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        timer!.Cancel();
        await Should.ThrowAsync<TransactionLockTimeoutException>(() => timed);
        outer.Dispose();
        timer = null;
        var untimed = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, ceiling.Token).AsTask();
        try
        {
            timer.ShouldBeNull();
            untimed.IsCompleted.ShouldBeFalse();
        }
        finally { locks.ReleaseAll(_holder); }
        await untimed.WaitAsync(ceiling.Token);

        // Assert
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
        var timed = DedicatedTestTask.Run(async () =>
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
        var applying = new LockManager();
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
        // Arrange: stop after dequeue but before notification, then cancel both deadlines.
        // Require that exact interleaving as well as cancellation/timeout winning the queue.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var locks = Create(true);
        int granted = 0, timedOut = 0, canceled = 0, iterations = 0, canceledAfterDequeue = 0;
        CancellationTokenSource? timer = null;
        locks.WaitTimerArmed = source => { source.CancelAfter(Timeout.InfiniteTimeSpan); timer = source; };
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, System.Diagnostics.Tracing.EventLevel.Verbose);
        long waits = TransactionEventSource.Log.TotalLockWaits;
        var elapsed = Stopwatch.StartNew();

        // Act
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || canceledAfterDequeue < 8 || timedOut < 8 || canceled < 8)
        {
            ceiling.Token.ThrowIfCancellationRequested();
            locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
            using var dequeued = new ManualResetEventSlim();
            using var notify = new ManualResetEventSlim();
            using var scope = locks.EnterLockTimeout(TimeSpan.FromHours(1));
            var pending = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, caller.Token).AsTask();
            locks.BeforeGrantNotification = () =>
            {
                dequeued.Set();
                notify.Wait(ceiling.Token);
            };
            Task release = Task.CompletedTask;
            try
            {
                switch (iterations % 3)
                {
                    case 0:
                        release = DedicatedTestTask.Run(() => locks.ReleaseAll(_holder), ceiling.Token);
                        dequeued.Wait(ceiling.Token);
                        pending.IsCompleted.ShouldBeFalse();
                        caller.Cancel();
                        timer!.Cancel();
                        pending.IsCompleted.ShouldBeFalse();
                        canceledAfterDequeue++;
                        notify.Set();
                        break;
                    case 1: timer!.Cancel(); break;
                    case 2: caller.Cancel(); break;
                }
                try { await pending.WaitAsync(ceiling.Token); granted++; }
                catch (TransactionLockTimeoutException) { timedOut++; }
                catch (OperationCanceledException) when (!ceiling.IsCancellationRequested) { canceled++; }
            }
            finally
            {
                notify.Set();
                await release;
                locks.BeforeGrantNotification = null;
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
        canceledAfterDequeue.ShouldBe(granted);
        timedOut.ShouldBeGreaterThanOrEqualTo(8);
        canceled.ShouldBeGreaterThanOrEqualTo(8);
        (granted + timedOut + canceled).ShouldBe(iterations);
        (TransactionEventSource.Log.TotalLockWaits - waits).ShouldBe(iterations);
        var stopped = recorder.Events.Where(e => e.EventName == "LockWaitStop").ToArray();
        stopped.Length.ShouldBe(iterations);
        stopped.Count(e => e.Payload![2]?.ToString() == "Granted").ShouldBe(granted);
        stopped.Count(e => e.Payload![2]?.ToString() == "Timeout").ShouldBe(timedOut);
        stopped.Count(e => e.Payload![2]?.ToString() == "Cancelled").ShouldBe(canceled);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - Lock timeout: a delayed continuation retains the cause that removed the wait")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireAsync_LaterCancellationBeforeContinuation_ShouldKeepFirstOutcome(bool callerFirst)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ceiling.Token);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var locks = Create(true);
        using var recorder = new EventSourceRecorder(TransactionEventSource.Log, System.Diagnostics.Tracing.EventLevel.Verbose);
        locks.TryAcquire(_holder, _row, LockMode.Exclusive).ShouldBeTrue();
        CancellationTokenSource? timer = null;
        locks.WaitTimerArmed = source => { source.CancelAfter(Timeout.InfiniteTimeSpan); timer = source; };
        using var scope = locks.EnterLockTimeout(TimeSpan.FromHours(1));
        int paused = 0;
        locks.BeforeWaitOutcome = () => { Interlocked.Increment(ref paused); entered.Set(); resume.Wait(ceiling.Token); };
        var pending = locks.AcquireAsync(_waiter, _row, LockMode.Exclusive, caller.Token).AsTask();

        // Act: the first completion has already left the queue before the other token changes.
        try
        {
            if (callerFirst) { caller.Cancel(); } else { timer!.Cancel(); }
            entered.Wait(ceiling.Token);
            if (callerFirst) { timer!.Cancel(); } else { caller.Cancel(); }
        }
        finally { resume.Set(); }

        // Assert
        if (callerFirst) { await Should.ThrowAsync<OperationCanceledException>(() => pending); }
        else { await Should.ThrowAsync<TransactionLockTimeoutException>(() => pending); }
        paused.ShouldBe(1);
        recorder.Events.Where(e => e.EventName == "LockWaitStop").ShouldHaveSingleItem()
            .Payload![2].ShouldBe(callerFirst ? "Cancelled" : "Timeout");
        locks.ReleaseAll(_holder);
        locks.TryAcquire(_probe, _row, LockMode.Exclusive).ShouldBeTrue();
        locks.ReleaseAll(_probe);
    }

    private static LockManager Create(bool engineMode)
    {
        var locks = new LockManager();
        if (engineMode) { locks.EnterEngineMode(_ => false, () => false, "timeout"); }
        return locks;
    }
}
