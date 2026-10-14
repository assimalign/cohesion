using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests;

/// <summary>
/// Unit tests for <see cref="MinDataRateWait"/> (#1384): an operation held to a minimum data rate arms a
/// deadline only when it actually waits, so the write that completes synchronously — nearly every response
/// write — costs nothing, while one that waits past the allowance is cancelled and charged.
/// </summary>
public class MinDataRateWaitTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly HttpMinDataRate _rate = new(bytesPerSecond: 1000, gracePeriod: TimeSpan.FromSeconds(1));

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateWait: An operation that completes synchronously should allocate no deadline")]
    public void TryRunAsync_OnOperationCompletingSynchronously_ShouldAllocateNothing()
    {
        // Arrange — the caller's token can be cancelled, as a host's always can. The first run creates the
        // gate's one reusable deadline and compiles the path.
        MinDataRateGate gate = new(_rate, TimeProvider.System);
        using CancellationTokenSource caller = new();
        RunSynchronously(gate, caller.Token).ShouldBeTrue();

        // Act
        bool completed = true;
        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0; index < 100; index++)
        {
            completed &= RunSynchronously(gate, caller.Token);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert — no timer, no linked source, no registration per operation.
        completed.ShouldBeTrue();
        allocated.ShouldBeLessThan(1024);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateWait: An operation that waits past the allowance should be cancelled and charged")]
    public async Task TryRunAsync_OnOperationWaitingPastAllowance_ShouldCancelAndReturnFalse()
    {
        // Arrange
        ManualTimeProvider clock = new();
        MinDataRateGate gate = new(_rate, clock);
        ValueTask<bool> run = MinDataRateWait.TryRunAsync(gate, 0, static (_, token) => new ValueTask(Task.Delay(Timeout.Infinite, token)), CancellationToken.None);
        await clock.WaitForTimersAsync(1);

        // Act — the whole grace period passes with nothing accepted.
        clock.Advance(_rate.GracePeriod);
        bool completed = await run.AsTask().WaitAsync(_timeout);

        // Assert
        completed.ShouldBeFalse();
        gate.GetRemainingTicks().ShouldBeLessThanOrEqualTo(0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateWait: A wait after a deadline fired should be bounded by a fresh deadline")]
    public async Task TryRunAsync_AfterDeadlineFired_ShouldCompleteNextWait()
    {
        // Arrange — a first wait runs out its allowance; the octets credited next buy the second one time.
        ManualTimeProvider clock = new();
        MinDataRateGate gate = new(_rate, clock);
        ValueTask<bool> first = MinDataRateWait.TryRunAsync(gate, 0, static (_, token) => new ValueTask(Task.Delay(Timeout.Infinite, token)), CancellationToken.None);
        await clock.WaitForTimersAsync(1);
        clock.Advance(_rate.GracePeriod);
        (await first.AsTask().WaitAsync(_timeout)).ShouldBeFalse();
        gate.Record(ticksWaited: 0, bytesTransferred: 5000);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        ValueTask<bool> second = MinDataRateWait.TryRunAsync(gate, accepted, static (signal, token) => new ValueTask(signal.Task.WaitAsync(token)), CancellationToken.None);
        bool pending = !second.IsCompleted;
        accepted.SetResult();

        // Assert
        pending.ShouldBeTrue();
        (await second.AsTask().WaitAsync(_timeout)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateWait: A caller that cancels a waiting operation should observe its own token")]
    public async Task TryRunAsync_OnCallerCancelling_ShouldThrowWithCallerToken()
    {
        // Arrange
        MinDataRateGate gate = new(_rate, new ManualTimeProvider());
        using CancellationTokenSource caller = new();
        ValueTask<bool> run = MinDataRateWait.TryRunAsync(gate, 0, static (_, token) => new ValueTask(Task.Delay(Timeout.Infinite, token)), caller.Token);

        // Act
        await caller.CancelAsync();

        // Assert
        OperationCanceledException failure = await Should.ThrowAsync<OperationCanceledException>(() => run.AsTask().WaitAsync(_timeout));
        failure.CancellationToken.ShouldBe(caller.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Connections] - MinDataRateWait: An operation whose allowance is already spent should not start")]
    public async Task TryRunAsync_OnAllowanceSpent_ShouldReturnFalseWithoutStarting()
    {
        // Arrange
        MinDataRateGate gate = new(_rate, new ManualTimeProvider());
        gate.Record(ticksWaited: TimeSpan.FromSeconds(2).Ticks, bytesTransferred: 0);
        StrongBox started = new();

        // Act
        bool completed = await MinDataRateWait.TryRunAsync(gate, started, static (box, _) => { box.Value = true; return ValueTask.CompletedTask; }, CancellationToken.None);

        // Assert
        completed.ShouldBeFalse();
        started.Value.ShouldBeFalse();
    }

    private static bool RunSynchronously(MinDataRateGate gate, CancellationToken cancellationToken)
    {
        ValueTask<bool> run = MinDataRateWait.TryRunAsync(gate, 0, static (_, _) => ValueTask.CompletedTask, cancellationToken);
        return run.IsCompletedSuccessfully && run.Result;
    }

    private sealed class StrongBox
    {
        public bool Value { get; set; }
    }
}
