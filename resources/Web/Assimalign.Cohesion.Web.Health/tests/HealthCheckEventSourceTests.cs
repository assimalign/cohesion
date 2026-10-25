using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Testing;
using Assimalign.Cohesion.Web.Health.Internal;

namespace Assimalign.Cohesion.Web.Health.Tests;

[Collection(nameof(HealthCheckEventSourceCollection))]
public class HealthCheckEventSourceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: is named for its assembly")]
    public void GetName_HealthCheckEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(HealthCheckEventSource));

        // Assert
        name.ShouldBe(typeof(HealthCheckEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Web.Health");
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: generates a strict manifest")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(HealthCheckEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Web.Health", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: reports each worsening and recovery once with declared payloads")]
    public async Task CheckHealthAsync_StatusChanges_ShouldReportTransitionsOnceAsync()
    {
        // Arrange
        string checkName = "lifecycle-" + Guid.NewGuid().ToString("N");
        var exception = new InvalidOperationException("Dependency db-primary.internal is unavailable.");
        HealthCheckResult result = HealthCheckResult.Degraded();
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck(checkName, () => result)
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        result = HealthCheckResult.Unhealthy(exception: exception);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        result = HealthCheckResult.Healthy();
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        EventWrittenEventArgs[] events = EventsForCheck(recorder, checkName);
        events.Select(e => e.EventId).ShouldBe([1, 1, 2]);
        events[0].EventName.ShouldBe("HealthCheckWorsened");
        events[0].Level.ShouldBe(EventLevel.Warning);
        events[0].PayloadNames.ShouldBe(["checkName", "previousStatus", "status", "cause", "exceptionType", "exceptionMessage"]);
        events[0].Payload.ShouldBe(new object?[] { checkName, "Unknown", "Degraded", "Reported", string.Empty, string.Empty });
        events[1].PayloadNames.ShouldBe(events[0].PayloadNames);
        events[1].Payload.ShouldBe(new object?[] { checkName, "Degraded", "Unhealthy", "Reported", typeof(InvalidOperationException).FullName, exception.Message });
        events[2].EventName.ShouldBe("HealthCheckImproved");
        events[2].Level.ShouldBe(EventLevel.Informational);
        events[2].PayloadNames.ShouldBe(["checkName", "previousStatus", "status"]);
        events[2].Payload.ShouldBe(new object?[] { checkName, "Unhealthy", "Healthy" });
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: first reported unhealthy result starts at Unknown")]
    public async Task CheckHealthAsync_FirstUnhealthyResult_ShouldReportUnknownWithReportedCauseAsync()
    {
        // Arrange
        string checkName = "first-unhealthy-" + Guid.NewGuid().ToString("N");
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck(checkName, () => HealthCheckResult.Unhealthy())
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        EventWrittenEventArgs worsened = EventsForCheck(recorder, checkName).ShouldHaveSingleItem();
        worsened.EventId.ShouldBe(1);
        worsened.Payload.ShouldBe(new object?[] { checkName, "Unknown", "Unhealthy", "Reported", string.Empty, string.Empty });
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: first healthy result is silent and records the observed status")]
    public async Task CheckHealthAsync_FirstHealthyResult_ShouldBeSilentAndSeedObservedStatusAsync()
    {
        // Arrange
        string checkName = "first-healthy-" + Guid.NewGuid().ToString("N");
        HealthCheckResult result = HealthCheckResult.Healthy();
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck(checkName, () => result)
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);

        // Act
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        EventWrittenEventArgs[] healthyEvents = EventsForCheck(recorder, checkName);
        result = HealthCheckResult.Unhealthy();
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        healthyEvents.ShouldBeEmpty();
        EventWrittenEventArgs worsened = EventsForCheck(recorder, checkName).ShouldHaveSingleItem();
        worsened.EventId.ShouldBe(1);
        worsened.Payload.ShouldBe(new object?[] { checkName, "Healthy", "Unhealthy", "Reported", string.Empty, string.Empty });
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: concurrent readiness and liveness probes share one transition slot")]
    public async Task CheckHealthAsync_ConcurrentReadyAndLiveProbes_ShouldReportEachTransitionOnceAsync()
    {
        // Arrange
        const int probeCount = 32;
        string checkName = "concurrent-" + Guid.NewGuid().ToString("N");
        HealthStatus status = HealthStatus.Unhealthy;
        int arrivals = 0;
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck("other-" + checkName, () => HealthCheckResult.Healthy(), tags: [HealthTags.Ready])
            .AddCheck(checkName, async (_, token) =>
            {
                if (Interlocked.Increment(ref arrivals) == probeCount)
                {
                    allEntered.SetResult();
                }

                await release.Task.WaitAsync(token);
                return new HealthCheckResult(status);
            }, tags: [HealthTags.Ready, HealthTags.Live])
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        foreach (HealthStatus nextStatus in new[] { HealthStatus.Unhealthy, HealthStatus.Healthy })
        {
            status = nextStatus;
            arrivals = 0;
            allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<HealthReport>[] probes = Enumerable.Range(0, probeCount)
                .Select(index => service.CheckHealthAsync(
                    index % 2 == 0 ? HealthCheckPredicates.Ready : HealthCheckPredicates.Live,
                    cancellation.Token).AsTask())
                .ToArray();
            await allEntered.Task.WaitAsync(cancellation.Token);
            release.SetResult();
            await Task.WhenAll(probes);
        }

        // Assert
        EventWrittenEventArgs[] events = EventsForCheck(recorder, checkName);
        events.Select(e => e.EventId).ShouldBe([1, 2]);
        events[0].Payload.ShouldBe(new object?[] { checkName, "Unknown", "Unhealthy", "Reported", string.Empty, string.Empty });
        events[1].Payload.ShouldBe(new object?[] { checkName, "Unhealthy", "Healthy" });
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: each built service owns independent transition slots")]
    public async Task Build_TwoServicesWithSameRegistration_ShouldTrackTransitionsIndependentlyAsync()
    {
        // Arrange
        string checkName = "instances-" + Guid.NewGuid().ToString("N");
        IHealthChecksBuilder builder = new HealthChecksBuilder()
            .AddCheck(checkName, new StubCheck(HealthStatus.Unhealthy));
        IHealthCheckService first = builder.Build();
        IHealthCheckService second = builder.Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);

        // Act
        await first.CheckHealthAsync(cancellationToken: cancellation.Token);
        await first.CheckHealthAsync(cancellationToken: cancellation.Token);
        await second.CheckHealthAsync(cancellationToken: cancellation.Token);
        await second.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        EventWrittenEventArgs[] events = EventsForCheck(recorder, checkName);
        events.Length.ShouldBe(2);
        foreach (EventWrittenEventArgs worsened in events)
        {
            worsened.EventId.ShouldBe(1);
            worsened.Payload.ShouldBe(new object?[] { checkName, "Unknown", "Unhealthy", "Reported", string.Empty, string.Empty });
        }

        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Health] - Event source: thrown and timed-out probes each report failure and count once")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckHealthAsync_RepeatedProbeFailures_ShouldReportEveryFailureAndOneTransitionAsync(bool timesOut)
    {
        // Arrange
        string checkName = "failure-" + Guid.NewGuid().ToString("N");
        const string message = "Dependency db-primary.internal could not connect.";
        TimeSpan timeout = TimeSpan.FromMilliseconds(timesOut ? 50 : 1234);
        string cause = timesOut ? "Timeout" : "Exception";
        string? exceptionType = timesOut ? typeof(OperationCanceledException).FullName : typeof(InvalidOperationException).FullName;
        StubCheck check = timesOut
            ? StubCheck.BlockingThenCancelledWith(message)
            : StubCheck.Throwing(new InvalidOperationException(message));
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck(checkName, check, failureStatus: HealthStatus.Degraded, timeout: timeout)
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        EventWrittenEventArgs[] events = EventsForCheck(recorder, checkName);
        EventWrittenEventArgs worsened = events.Where(e => e.EventId == 1).ShouldHaveSingleItem();
        worsened.Payload.ShouldBe(new object?[] { checkName, "Unknown", "Degraded", cause, exceptionType, message });
        EventWrittenEventArgs[] failures = events.Where(e => e.EventId == 3).ToArray();
        failures.Length.ShouldBe(2);
        foreach (EventWrittenEventArgs failure in failures)
        {
            failure.EventName.ShouldBe("HealthCheckFailed");
            failure.Level.ShouldBe(EventLevel.Verbose);
            failure.PayloadNames.ShouldBe(["checkName", "failureStatus", "cause", "exceptionType", "exceptionMessage", "timeoutMilliseconds"]);
            failure.Payload.ShouldBe(new object?[] { checkName, "Degraded", cause, exceptionType, message, timeout.TotalMilliseconds });
        }

        events.Length.ShouldBe(3);
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore + 2);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: counts failed probes while no listener is enabled")]
    public async Task CheckHealthAsync_NoListener_ShouldCountOnlyThrownAndTimedOutChecksAsync()
    {
        // Arrange
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck("exception", StubCheck.Throwing(new InvalidOperationException("offline")))
            .AddCheck("timeout", StubCheck.Blocking(), timeout: TimeSpan.FromMilliseconds(50))
            .AddCheck("reported", new StubCheck(HealthStatus.Unhealthy))
            .AddCheck("healthy", new StubCheck(HealthStatus.Healthy))
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        HealthCheckEventSource.Log.IsEnabled().ShouldBeFalse();
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore + 4);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: caller cancellation does not report a failure or consume the initial transition")]
    public async Task CheckHealthAsync_CallerCancellation_ShouldLeaveCounterAndTransitionSlotUnchangedAsync()
    {
        // Arrange
        string checkName = "cancelled-" + Guid.NewGuid().ToString("N");
        bool block = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IHealthCheckService service = new HealthChecksBuilder()
            .AddCheck(checkName, async (_, token) =>
            {
                if (block)
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return HealthCheckResult.Unhealthy();
            })
            .Build();
        using var cancellation = new CancellationTokenSource(_testTimeout);
        using var callerCancellation = new CancellationTokenSource(_testTimeout);
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.Verbose);
        long failedBefore = HealthCheckEventSource.Log.FailedChecks;

        // Act
        Task<HealthReport> probe = service.CheckHealthAsync(cancellationToken: callerCancellation.Token).AsTask();
        await entered.Task.WaitAsync(cancellation.Token);
        callerCancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await probe);
        EventWrittenEventArgs[] cancelledEvents = EventsForCheck(recorder, checkName);
        block = false;
        await service.CheckHealthAsync(cancellationToken: cancellation.Token);

        // Assert
        cancelledEvents.ShouldBeEmpty();
        HealthCheckEventSource.Log.FailedChecks.ShouldBe(failedBefore);
        EventWrittenEventArgs worsened = EventsForCheck(recorder, checkName).ShouldHaveSingleItem();
        worsened.EventId.ShouldBe(1);
        worsened.Payload.ShouldBe(new object?[] { checkName, "Unknown", "Unhealthy", "Reported", string.Empty, string.Empty });
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Health] - Event source: publishes the failed checks counter")]
    public async Task Counters_EnabledWithInterval_ShouldPublishFailedChecksAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(_testTimeout);

        // Act
        using var recorder = new EventSourceRecorder(HealthCheckEventSource.Log, EventLevel.LogAlways, TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(["failed-checks"], cancellation.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static EventWrittenEventArgs[] EventsForCheck(EventSourceRecorder recorder, string checkName)
        => recorder.Events.Where(e => e.Payload is { Count: > 0 } && Equals(e.Payload[0], checkName)).ToArray();
}
