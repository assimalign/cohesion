using System;
using System.Collections.Generic;
using System.Threading;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class WorkerPumpObservationTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Worker observations: a failed trigger is measured before its recovery delay")]
    public void Run_TriggerThrows_ShouldCompleteItsMeasurementBeforeBackoff()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var order = new List<string>();
        var worker = new ScriptedWorker((_, _) => { }, TimeSpan.Zero) { FailTriggers = 1 };
        using var observation = WorkerPumpObservation.Create(worker,
            _ => { order.Add("backoff"); stop.Cancel(); }, _ => order.Add("trigger"));

        // Act
        worker.Run(stop.Token);

        // Assert
        order.ShouldBe(["trigger", "backoff"]);
        observation.Failure.ShouldBeNull();
        worker.Passes.ShouldBe(0);
        worker.FailureCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Worker observations: callback failures cannot change a delay or pump outcome")]
    public void Run_ObserverThrows_ShouldKeepRunningAndRetainTheObserverFailure()
    {
        // Arrange
        using var stop = new CancellationTokenSource();
        var failure = new InvalidOperationException("observer failed");
        var worker = new ScriptedWorker((_, pass) => { if (pass == 3) { stop.Cancel(); } }, TimeSpan.Zero);
        using var observation = WorkerPumpObservation.Create(worker, _ => throw failure, _ => throw failure);

        // Act
        worker.Delay(TimeSpan.Zero, CancellationToken.None);
        var delayFailure = observation.Failure;
        worker.Run(stop.Token);

        // Assert
        delayFailure.ShouldBeSameAs(failure);
        observation.Failure.ShouldBeSameAs(failure);
        worker.Passes.ShouldBe(3);
        worker.Fault.ShouldBeNull();
        worker.FailureCount.ShouldBe(0);
    }
}
