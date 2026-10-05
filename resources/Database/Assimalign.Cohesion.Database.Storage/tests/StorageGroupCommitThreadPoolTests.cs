using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Grouped commit durability with the thread pool held: a grouped commit's registration on the
/// gate and the flush worker's pass that releases it need no pool thread, so a pool other work has
/// filled delays neither. The tests run alone (<see cref="ThreadPoolSaturationCollection"/>).
/// </summary>
[Collection(ThreadPoolSaturationCollection.Name)]
public sealed class StorageGroupCommitThreadPoolTests
{
    /// <summary>
    /// The worker test's scenario (<see cref="GroupedCommitScenario.AssertCompletesOnWorkerFlush"/>)
    /// with every pool worker parked and a queue behind them. Its committer used to start with
    /// <c>Task.Run</c>: held this way, it never started and the registration wait timed out at
    /// 10 s: the failure CI showed at 64c4b831, most likely because parallel test classes filled the pool.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - GroupCommit: A grouped commit registers and completes on the worker's flush while the thread pool runs nothing")]
    public void Commit_GroupedWithWorkerAndThreadPoolHeld_ShouldCompleteWhenWorkerFlushes()
    {
        // Arrange
        using var saturation = ThreadPoolSaturation.Start();

        // Act / Assert: the whole scenario, with its own assertions.
        GroupedCommitScenario.AssertCompletesOnWorkerFlush();

        // Assert: nothing queued to the pool since the saturation began has run, so the scenario
        // passed without it.
        saturation.HasHeld.ShouldBeTrue();
    }
}
