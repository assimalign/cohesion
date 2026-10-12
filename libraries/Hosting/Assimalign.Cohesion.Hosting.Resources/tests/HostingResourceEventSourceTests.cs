using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Hosting.Health;
using Assimalign.Cohesion.Hosting.Resources.Internal;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Hosting.Resources.Tests;

[Collection(nameof(SerialCollection))]
public sealed class HostingResourceEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: the provider name is its assembly")]
    public void GetName_HostingResourceEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(HostingResourceEventSource));

        // Assert
        name.ShouldBe("Assimalign.Cohesion.Hosting.Resources");
        name.ShouldBe(typeof(HostingResourceEventSource).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: the strict manifest is valid")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation; the test assembly is not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation; the test assembly is not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(HostingResourceEventSource), null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Hosting.Resources", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: a contributor failure reaches diagnostics once while the report retains fixed text")]
    public async Task CheckHealthAsync_ContributorThrows_ShouldReportFailureOnceWithoutChangingPublicReport()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        const string contributorName = "event-source-contributor-hygiene";
        const string message = "Dependency db-primary.corp.internal failed.";
        var exception = new InvalidOperationException(message, new Exception("Inner exception is never written."));
        IResourceControlPlane plane = CreateFailingPlane(contributorName, exception);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long before = HostingResourceEventSource.Log.ContributorFailures;

        // Act
        ResourceHealthReport report = await plane.CheckHealthAsync(cancellation.Token);

        // Assert
        HostingResourceEventSource.Log.ContributorFailures.ShouldBe(before + 1);
        report.Contributions[contributorName].Description.ShouldBe(DefaultResourceControlPlane.ContributorThrewDescription);
        report.Contributions[contributorName].Data.ShouldBeNull();
        EventWrittenEventArgs failed = recorder.Events.Where(item => IsContributorFailure(item, contributorName)).ShouldHaveSingleItem();
        failed.EventId.ShouldBe(1);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["contributorName", "exceptionType", "exceptionMessage", "suppressedCount"]);
        failed.Payload![1].ShouldBe(typeof(InvalidOperationException).FullName);
        failed.Payload[2].ShouldBe(message);
        failed.Payload.ShouldNotContain("Inner exception is never written.");
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: failures count without a listener and the counter publishes when enabled")]
    public async Task Counters_FailuresBeforeListener_ShouldRemainExactAndPublish()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IResourceControlPlane plane = CreateFailingPlane("event-source-counter", new InvalidOperationException("dependency failed"));
        long before = HostingResourceEventSource.Log.ContributorFailures;

        // Act
        await plane.CheckHealthAsync(cancellation.Token);
        await plane.CheckReadinessAsync(cancellation.Token);
        await plane.CheckLivenessAsync(cancellation.Token);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Assert
        HostingResourceEventSource.Log.ContributorFailures.ShouldBe(before + 3);
        await recorder.WaitForCountersAsync(["contributor-failures"], cancellation.Token);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: repeated failures are throttled and suppressed occurrences remain counted")]
    public async Task CheckHealthAsync_RepeatedFailures_ShouldThrottleEventsAndCountEveryFailure()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        const string contributorName = "event-source-throttling";
        IResourceControlPlane plane = CreateFailingPlane(contributorName, new InvalidOperationException("dependency failed"));
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long before = HostingResourceEventSource.Log.ContributorFailures;

        // Act
        await plane.CheckHealthAsync(cancellation.Token);
        await plane.CheckReadinessAsync(cancellation.Token);
        await plane.CheckLivenessAsync(cancellation.Token);
        recorder.Events.Where(item => IsContributorFailure(item, contributorName)).ShouldHaveSingleItem();
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        await plane.CheckHealthAsync(cancellation.Token);

        // Assert
        HostingResourceEventSource.Log.ContributorFailures.ShouldBe(before + 4);
        EventWrittenEventArgs[] events = recorder.Events.Where(item => IsContributorFailure(item, contributorName)).ToArray();
        events.Length.ShouldBe(2);
        events[1].Payload![3].ShouldBe(2);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: requested cancellation does not become a contributor failure")]
    public async Task CheckHealthAsync_CallerCancels_ShouldPropagateWithoutFailureEventOrCounter()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string contributorName = "event-source-caller-cancellation";
        IResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddHealthContributor(new CallbackContributor(contributorName, token =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<HealthContribution>(new OperationCanceledException(token));
        }));
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long before = HostingResourceEventSource.Log.ContributorFailures;

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => plane.CheckHealthAsync(cancellation.Token).AsTask());

        // Assert
        HostingResourceEventSource.Log.ContributorFailures.ShouldBe(before);
        recorder.Events.ShouldNotContain(item => IsContributorFailure(item, contributorName));
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static IResourceControlPlane CreateFailingPlane(string contributorName, Exception exception)
    {
        IResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddHealthContributor(new CallbackContributor(contributorName, _ => ValueTask.FromException<HealthContribution>(exception)));
        return plane;
    }

    private static bool IsContributorFailure(EventWrittenEventArgs item, string contributorName)
        => item.EventName == "ContributorFailed" && Equals(item.Payload![0], contributorName);

    private sealed class CallbackContributor : IHealthContributor
    {
        private readonly Func<CancellationToken, ValueTask<HealthContribution>> _callback;

        internal CallbackContributor(string name, Func<CancellationToken, ValueTask<HealthContribution>> callback)
        {
            Name = name;
            _callback = callback;
        }

        public string Name { get; }

        public ValueTask<HealthContribution> CheckAsync(CancellationToken cancellationToken = default)
            => _callback(cancellationToken);
    }
}
