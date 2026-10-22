using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class DedicatedTestTaskTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Test actors: async driver continuations progress on their own thread while the pool is held (#1285)")]
    public async Task Run_AsyncYieldWhilePoolHeld_ShouldKeepTheActorThread()
    {
        // Arrange
        using var saturation = ThreadPoolSaturation.Start();
        var threads = new int[4];
        bool pooled = false;
        bool context = false;

        // Act
        var actor = DedicatedTestTask.Run(async () =>
        {
            for (int index = 0; index < threads.Length; index++)
            {
                await Task.Yield();
                threads[index] = Environment.CurrentManagedThreadId;
                pooled |= Thread.CurrentThread.IsThreadPoolThread;
                context |= SynchronizationContext.Current is not null;
            }
        });
        // Hang guard only; no duration decides the actor's correctness.
        bool completed = ((IAsyncResult)actor).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        bool held = saturation.HasHeld;
        saturation.Dispose();
        await actor;

        // Assert
        held.ShouldBeTrue();
        completed.ShouldBeTrue();
        pooled.ShouldBeFalse();
        context.ShouldBeFalse();
        threads.Distinct().ShouldHaveSingleItem().ShouldNotBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Test actors: failures preserve identity and cancelled starts run no work (#1285)")]
    public async Task Run_FailureAndCancelledStart_ShouldPreserveTheirOutcomes()
    {
        // Arrange
        var failure = new InvalidOperationException("actor failure");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool ran = false;

        // Act / Assert
        (await Should.ThrowAsync<InvalidOperationException>(() => DedicatedTestTask.Run(async () =>
        {
            await Task.Yield();
            throw failure;
        }))).ShouldBeSameAs(failure);
        await Should.ThrowAsync<OperationCanceledException>(() => DedicatedTestTask.Run(() => { ran = true; }, cancelled.Token));
        ran.ShouldBeFalse();
        var cancellation = new OperationCanceledException(cancelled.Token);
        var cancelling = DedicatedTestTask.Run(async () => { await Task.Yield(); throw cancellation; });
        OperationCanceledException? observed = null;
        try { await cancelling; }
        catch (OperationCanceledException failureObserved) { observed = failureObserved; }
        observed.ShouldBeSameAs(cancellation);
        cancelling.IsCanceled.ShouldBeTrue();
    }
}
