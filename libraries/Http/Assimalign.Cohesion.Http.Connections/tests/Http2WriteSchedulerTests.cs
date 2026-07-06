using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal.Http2;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// RFC 9218 §10 scheduling-policy tests for <see cref="Http2WriteScheduler"/>:
/// control frames outrank response writes, response writes are ordered by
/// ascending urgency, non-incremental precedes incremental at equal urgency,
/// and same-urgency incremental streams round-robin by stream id. Also covers
/// the async gate's granting and cancellation behavior.
/// </summary>
public class Http2WriteSchedulerTests
{
    private static Http2WriteScheduler.Waiter Waiter(int streamId, int urgency, bool incremental, long sequence)
        => new(streamId, urgency, incremental, sequence);

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Control frames outrank response writes")]
    public void SelectNext_ControlFrame_ShouldWinOverMostUrgentResponse()
    {
        List<Http2WriteScheduler.Waiter> waiters = new()
        {
            Waiter(1, urgency: 0, incremental: false, sequence: 0),      // most-urgent response
            Waiter(0, Http2WriteScheduler.ControlUrgency, false, 1),     // control frame
        };

        Http2WriteScheduler.SelectNextWaiterIndex(waiters, -1).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Lower urgency is served first")]
    public void SelectNext_LowerUrgency_ShouldWin()
    {
        List<Http2WriteScheduler.Waiter> waiters = new()
        {
            Waiter(1, urgency: 5, incremental: false, sequence: 0),
            Waiter(3, urgency: 1, incremental: false, sequence: 1),
            Waiter(5, urgency: 3, incremental: false, sequence: 2),
        };

        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, -1)].StreamId.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Non-incremental precedes incremental at equal urgency")]
    public void SelectNext_NonIncremental_ShouldPrecedeIncremental()
    {
        List<Http2WriteScheduler.Waiter> waiters = new()
        {
            Waiter(1, urgency: 3, incremental: true, sequence: 0),
            Waiter(3, urgency: 3, incremental: false, sequence: 1),
        };

        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, -1)].StreamId.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Non-incremental ties break by arrival order")]
    public void SelectNext_NonIncrementalTie_ShouldBreakByArrival()
    {
        List<Http2WriteScheduler.Waiter> waiters = new()
        {
            Waiter(5, urgency: 3, incremental: false, sequence: 2),
            Waiter(1, urgency: 3, incremental: false, sequence: 0),
            Waiter(3, urgency: 3, incremental: false, sequence: 1),
        };

        // Earliest arrival (sequence 0) wins regardless of list position.
        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, -1)].StreamId.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Same-urgency incremental streams round-robin by id")]
    public void SelectNext_IncrementalStreams_ShouldRoundRobinByStreamId()
    {
        List<Http2WriteScheduler.Waiter> waiters = new()
        {
            Waiter(1, urgency: 3, incremental: true, sequence: 0),
            Waiter(3, urgency: 3, incremental: true, sequence: 1),
            Waiter(5, urgency: 3, incremental: true, sequence: 2),
        };

        // Fresh rotation → smallest id.
        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, -1)].StreamId.ShouldBe(1);
        // After serving 1 → next id above 1.
        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, 1)].StreamId.ShouldBe(3);
        // After serving 3 → next id above 3.
        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, 3)].StreamId.ShouldBe(5);
        // After serving 5 → no larger id, wrap to the smallest.
        waiters[Http2WriteScheduler.SelectNextWaiterIndex(waiters, 5)].StreamId.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: A free gate is granted immediately")]
    public async Task Acquire_FreeGate_ShouldCompleteImmediately()
    {
        using Http2WriteScheduler scheduler = new();

        Task acquire = scheduler.AcquireAsync(1, urgency: 3, incremental: false, CancellationToken.None);

        acquire.IsCompleted.ShouldBeTrue();
        await acquire;
        scheduler.Release();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: Contended writers are granted in urgency order")]
    public async Task Acquire_Contended_ShouldGrantInUrgencyOrder()
    {
        using Http2WriteScheduler scheduler = new();

        // Hold the gate so the following three writers must queue.
        await scheduler.AcquireAsync(0, Http2WriteScheduler.ControlUrgency, false, CancellationToken.None);

        Task low = scheduler.AcquireAsync(1, urgency: 5, incremental: false, CancellationToken.None);
        Task high = scheduler.AcquireAsync(3, urgency: 1, incremental: false, CancellationToken.None);
        Task mid = scheduler.AcquireAsync(5, urgency: 3, incremental: false, CancellationToken.None);

        low.IsCompleted.ShouldBeFalse();
        high.IsCompleted.ShouldBeFalse();
        mid.IsCompleted.ShouldBeFalse();

        // Release the control write → the lowest-urgency response (stream 3) wins.
        scheduler.Release();
        await high;
        low.IsCompleted.ShouldBeFalse();
        mid.IsCompleted.ShouldBeFalse();

        scheduler.Release();
        await mid;
        low.IsCompleted.ShouldBeFalse();

        scheduler.Release();
        await low;

        scheduler.Release();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - Http2 scheduler: A cancelled queued acquisition is withdrawn")]
    public async Task Acquire_CancelledWhileQueued_ShouldCancel()
    {
        using Http2WriteScheduler scheduler = new();

        await scheduler.AcquireAsync(0, Http2WriteScheduler.ControlUrgency, false, CancellationToken.None);

        using CancellationTokenSource cts = new();
        Task queued = scheduler.AcquireAsync(1, urgency: 3, incremental: false, cts.Token);
        queued.IsCompleted.ShouldBeFalse();

        cts.Cancel();
        await Should.ThrowAsync<TaskCanceledException>(async () => await queued);

        // The gate is still held by the control writer; releasing it must not
        // fault even though the queued waiter was withdrawn.
        scheduler.Release();
    }
}
