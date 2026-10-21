using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Tests;

public sealed class WorkerPumpObservationTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Worker observations: raw pump waits cannot bypass requested-delay records")]
    public void FindUnobservedWaits_RawPumpPause_ShouldRejectEveryUnobservedWait()
    {
        // Arrange
        const string source = """
            class Worker
            {
                void Delay() => token.WaitHandle.WaitOne(duration);
                void WaitForTrigger() => token.WaitHandle.WaitOne(interval);
                void Run()
                {
                    Delay();
                    token.WaitHandle.WaitOne(250);
                    Thread.Sleep(250);
                    Sleep(250);
                    timer?.Wait();
                    Task.Delay(250).Wait();
                }
            }
            """;

        // Act
        string[] waits = WorkerPumpObservation.FindUnobservedWaits(source, "Delay", "WaitForTrigger");

        // Assert: all six raw calls are refused; the observed delay and trigger are allowed.
        waits.Length.ShouldBe(6);
        waits.ShouldAllBe(wait => wait.StartsWith("Run: ", System.StringComparison.Ordinal));
    }
}
