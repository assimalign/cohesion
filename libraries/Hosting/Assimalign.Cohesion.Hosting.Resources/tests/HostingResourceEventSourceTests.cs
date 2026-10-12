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

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: a throwing exception message cannot replace the fixed health report")]
    public async Task CheckHealthAsync_ThrowingExceptionMessage_ShouldRetainFixedReportAndSafeDiagnostic()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        const string contributorName = "event-source-throwing-message";
        IResourceControlPlane plane = CreateFailingPlane(contributorName, new ThrowingMessageException());
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long before = HostingResourceEventSource.Log.ContributorFailures;

        // Act
        ResourceHealthReport report = await plane.CheckHealthAsync(cancellation.Token);

        // Assert
        HostingResourceEventSource.Log.ContributorFailures.ShouldBe(before + 1);
        report.Contributions[contributorName].Description.ShouldBe(DefaultResourceControlPlane.ContributorThrewDescription);
        report.Contributions[contributorName].Data.ShouldBeNull();
        EventWrittenEventArgs failed = recorder.Events.Where(item => IsContributorFailure(item, contributorName)).ShouldHaveSingleItem();
        failed.Payload![1].ShouldBe(typeof(ThrowingMessageException).FullName);
        failed.Payload[2].ShouldBe("The exception message could not be read.");
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

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: command outcomes are emitted once with registered kinds and no command data")]
    public async Task ExecuteCommandAsync_CommandOutcomes_ShouldEmitOnceWithRegisteredKindsOnly()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        string kind = "registered-" + Guid.NewGuid().ToString("N");
        var handler = new DiagnosticCommandHandler(kind);
        IResourceControlPlane plane = CreateCommandPlane(handler);
        ResourceCommand command = CreateCommand(kind);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long[] before = ReadCommandCounters();

        // Act
        await plane.ExecuteCommandAsync(command, cancellation.Token);
        await Should.ThrowAsync<NotSupportedException>(() => plane.ExecuteCommandAsync(command with { Kind = "unknown-kind-secret" }, cancellation.Token).AsTask());
        await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.ExecuteCommandAsync(command with { Owner = "another-owner-secret" }, cancellation.Token).AsTask());
        handler.Failure = new ArgumentException("handler-payload-secret", "handler-key-secret");
        await Should.ThrowAsync<ArgumentException>(() => plane.ExecuteCommandAsync(command with { Id = "failure-id-secret", Key = "failure-key-secret" }, cancellation.Token).AsTask());

        // Assert
        AssertCommandCounterChanges(before, applied: 1, unknown: 1, conflicts: 1, failures: 1);
        EventWrittenEventArgs applied = ForCommand(recorder, "CommandApplied", kind).ShouldHaveSingleItem();
        AssertCommandEvent(applied, 2, EventLevel.Informational, ["kind", "operation"]);
        EventWrittenEventArgs unknown = ForCommand(recorder, "CommandUnknownKind", string.Empty).ShouldHaveSingleItem();
        AssertCommandEvent(unknown, 3, EventLevel.Warning, ["kind", "operation", "suppressedCount"]);
        EventWrittenEventArgs conflict = ForCommand(recorder, "CommandConflict", kind).ShouldHaveSingleItem();
        AssertCommandEvent(conflict, 4, EventLevel.Warning, ["kind", "operation", "suppressedCount"]);
        EventWrittenEventArgs failure = ForCommand(recorder, "CommandHandlerFailed", kind).ShouldHaveSingleItem();
        AssertCommandEvent(failure, 5, EventLevel.Error, ["kind", "operation", "exceptionType", "suppressedCount"]);
        failure.Payload![2].ShouldBe(typeof(ArgumentException).FullName);
        foreach (EventWrittenEventArgs item in new[] { applied, unknown, conflict, failure })
        {
            item.Payload![1].ShouldBe("Apply");
            string.Join("|", item.Payload).ShouldNotContain("secret", Case.Sensitive);
        }
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: replay, delete and handler refusal counters stay exact without a listener")]
    public async Task Counters_CommandOutcomesBeforeListener_ShouldCountEveryOutcomeAndPublish()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string kind = "counter-" + Guid.NewGuid().ToString("N");
        var handler = new DiagnosticCommandHandler(kind);
        IResourceControlPlane plane = CreateCommandPlane(handler);
        ResourceCommand command = CreateCommand(kind);
        long[] before = ReadCommandCounters();

        // Act
        await plane.ExecuteCommandAsync(command, cancellation.Token);
        await plane.ExecuteCommandAsync(command, cancellation.Token);
        await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.ExecuteCommandAsync(command with { Owner = "other-owner-secret" }, cancellation.Token).AsTask());
        handler.Failure = new ResourceCommandRejectedException("provider-refusal-secret");
        await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.ExecuteCommandAsync(command with { Id = "refusal-id-secret", Key = "refusal-key-secret" }, cancellation.Token).AsTask());
        handler.Failure = new InvalidOperationException("handler-failure-secret");
        await Should.ThrowAsync<InvalidOperationException>(() => plane.ExecuteCommandAsync(command with { Id = "failure-id-secret", Key = "failure-key-secret" }, cancellation.Token).AsTask());
        handler.Failure = null;
        await plane.DeleteCommandAsync(command, cancellation.Token);
        await plane.DeleteCommandAsync(command, cancellation.Token);
        await Should.ThrowAsync<NotSupportedException>(() => plane.DeleteCommandAsync(command with { Kind = "advertised-kind-secret" }, cancellation.Token).AsTask());
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Assert
        AssertCommandCounterChanges(before, applied: 4, unknown: 1, conflicts: 2, failures: 1);
        handler.Executions.ShouldBe(3); // Initial apply and the two rejected handler calls; replay never executes.
        handler.Deletions.ShouldBe(1);
        await recorder.WaitForCountersAsync(["contributor-failures", "commands-applied", "unknown-command-kinds", "command-conflicts", "command-handler-failures"], cancellation.Token);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: command refusal and failure throttles are independent with exact counters")]
    public async Task ExecuteCommandAsync_RepeatedCommandFailures_ShouldThrottleEachEventAndCountEveryOutcome()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        string kind = "throttle-" + Guid.NewGuid().ToString("N");
        var handler = new DiagnosticCommandHandler(kind);
        IResourceControlPlane plane = CreateCommandPlane(handler);
        ResourceCommand command = CreateCommand(kind);
        await plane.ExecuteCommandAsync(command, cancellation.Token);
        handler.Failure = new InvalidOperationException("handler-payload-secret");
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long[] before = ReadCommandCounters();

        async Task FailEachAsync()
        {
            await Should.ThrowAsync<NotSupportedException>(() => plane.DeleteCommandAsync(command with { Kind = "unknown-kind-secret" }, cancellation.Token).AsTask());
            await Should.ThrowAsync<ResourceCommandRejectedException>(() => plane.DeleteCommandAsync(command with { Owner = "other-owner-secret" }, cancellation.Token).AsTask());
            await Should.ThrowAsync<InvalidOperationException>(() => plane.DeleteCommandAsync(command, cancellation.Token).AsTask());
        }

        // Act
        await FailEachAsync();
        await FailEachAsync();
        await FailEachAsync();
        ForCommand(recorder, "CommandUnknownKind", string.Empty).ShouldHaveSingleItem();
        ForCommand(recorder, "CommandConflict", kind).ShouldHaveSingleItem();
        ForCommand(recorder, "CommandHandlerFailed", kind).ShouldHaveSingleItem();
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        await FailEachAsync();

        // Assert
        AssertCommandCounterChanges(before, applied: 0, unknown: 4, conflicts: 4, failures: 4);
        foreach ((string name, string eventKind) in new[] { ("CommandUnknownKind", string.Empty), ("CommandConflict", kind), ("CommandHandlerFailed", kind) })
        {
            EventWrittenEventArgs[] events = ForCommand(recorder, name, eventKind);
            events.Length.ShouldBe(2);
            events[1].Payload![1].ShouldBe("Delete");
            events[1].Payload![events[1].PayloadNames!.IndexOf("suppressedCount")].ShouldBe(2);
            string.Join("|", events[1].Payload!).ShouldNotContain("secret", Case.Sensitive);
        }
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: concurrent replays and deletions each report success once")]
    public async Task ExecuteCommandAsync_ConcurrentReplayAndDelete_ShouldCountDispatchesWithoutRepeatingHandlers()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string kind = "concurrent-" + Guid.NewGuid().ToString("N");
        var handler = new DiagnosticCommandHandler(kind)
        {
            Callback = async _ =>
            {
                await Task.Yield();
                return ReadOnlyMemory<byte>.Empty;
            },
        };
        IResourceControlPlane plane = CreateCommandPlane(handler);
        ResourceCommand command = CreateCommand(kind);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long[] before = ReadCommandCounters();

        // Act
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => plane.ExecuteCommandAsync(command, cancellation.Token).AsTask()));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => plane.DeleteCommandAsync(command, cancellation.Token).AsTask()));

        // Assert
        AssertCommandCounterChanges(before, applied: 24, unknown: 0, conflicts: 0, failures: 0);
        handler.Executions.ShouldBe(1);
        handler.Deletions.ShouldBe(1);
        EventWrittenEventArgs[] applied = ForCommand(recorder, "CommandApplied", kind);
        applied.Length.ShouldBe(24);
        applied.Count(item => Equals(item.Payload![1], "Apply")).ShouldBe(16);
        applied.Count(item => Equals(item.Payload![1], "Delete")).ShouldBe(8);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: caller cancellation in a handler produces no failure outcome")]
    public async Task ExecuteCommandAsync_CallerCancelsHandler_ShouldPreserveCancellationWithoutOutcome()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string kind = "cancellation-" + Guid.NewGuid().ToString("N");
        var handler = new DiagnosticCommandHandler(kind)
        {
            Callback = token =>
            {
                cancellation.Cancel();
                return ValueTask.FromException<ReadOnlyMemory<byte>>(new OperationCanceledException(token));
            },
        };
        IResourceControlPlane plane = CreateCommandPlane(handler);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long[] before = ReadCommandCounters();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => plane.ExecuteCommandAsync(CreateCommand(kind), cancellation.Token).AsTask());

        // Assert
        AssertCommandCounterChanges(before, applied: 0, unknown: 0, conflicts: 0, failures: 0);
        recorder.Events.ShouldNotContain(item => item.Payload != null && item.Payload.Contains(kind));
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Hosting.Resources] - Event source: handler unsupported operations and independent cancellation are failures, not unknown kinds")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteCommandAsync_HandlerThrowsSpecialException_ShouldReportHandlerFailure(bool independentCancellation)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), cancellation.Token);
        string kind = "failure-" + Guid.NewGuid().ToString("N");
        Exception exception = independentCancellation
            ? new OperationCanceledException("independent-cancellation-secret")
            : new NotSupportedException("handler-unsupported-secret");
        var handler = new DiagnosticCommandHandler(kind) { Failure = exception };
        IResourceControlPlane plane = CreateCommandPlane(handler);
        using var recorder = new EventSourceRecorder(HostingResourceEventSource.Log, EventLevel.Verbose);
        long[] before = ReadCommandCounters();

        // Act
        Exception actual = await Should.ThrowAsync<Exception>(() => plane.ExecuteCommandAsync(CreateCommand(kind), cancellation.Token).AsTask());

        // Assert
        if (independentCancellation)
        {
            (actual is OperationCanceledException).ShouldBeTrue();
        }
        else
        {
            actual.ShouldBeSameAs(exception);
        }
        AssertCommandCounterChanges(before, applied: 0, unknown: 0, conflicts: 0, failures: 1);
        EventWrittenEventArgs failure = ForCommand(recorder, "CommandHandlerFailed", kind).ShouldHaveSingleItem();
        failure.Payload![2].ShouldBe(exception.GetType().FullName);
        string.Join("|", failure.Payload).ShouldNotContain("secret", Case.Sensitive);
        recorder.Events.ShouldNotContain(item => item.EventId == 0);
    }

    private static ResourceCommand CreateCommand(string kind)
        => new("id-secret", kind, "owner-secret", "key-secret", "payload-secret"u8.ToArray());

    private static IResourceControlPlane CreateCommandPlane(DiagnosticCommandHandler handler)
    {
        IResourceControlPlane plane = ResourceControlPlane.Create([handler.Kind, "advertised-kind-secret"]);
        plane.RegisterCommandHandler(handler);
        return plane;
    }

    private static long[] ReadCommandCounters()
    {
        HostingResourceEventSource source = HostingResourceEventSource.Log;
        return [source.CommandsApplied, source.UnknownCommandKinds, source.CommandConflicts, source.CommandHandlerFailures];
    }

    private static void AssertCommandCounterChanges(long[] before, long applied, long unknown, long conflicts, long failures)
        => ReadCommandCounters().ShouldBe(new[] { before[0] + applied, before[1] + unknown, before[2] + conflicts, before[3] + failures });

    private static EventWrittenEventArgs[] ForCommand(EventSourceRecorder recorder, string name, string kind)
        => recorder.Events.Where(item => item.EventName == name && Equals(item.Payload![0], kind)).ToArray();

    private static void AssertCommandEvent(EventWrittenEventArgs item, int id, EventLevel level, string[] payloadNames)
    {
        item.EventId.ShouldBe(id);
        item.Level.ShouldBe(level);
        item.PayloadNames.ShouldBe(payloadNames);
    }

    private sealed class DiagnosticCommandHandler : IResourceCommandHandler
    {
        internal DiagnosticCommandHandler(string kind) => Kind = kind;

        public string Kind { get; }
        internal Exception? Failure { get; set; }
        internal Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>>? Callback { get; init; }
        internal int Executions { get; private set; }
        internal int Deletions { get; private set; }

        public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
        {
            Executions++;
            return CompleteAsync(cancellationToken);
        }

        public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
        {
            Deletions++;
            return CompleteAsync(cancellationToken);
        }

        private ValueTask<ReadOnlyMemory<byte>> CompleteAsync(CancellationToken cancellationToken)
            => Failure is { } exception
                ? ValueTask.FromException<ReadOnlyMemory<byte>>(exception)
                : Callback?.Invoke(cancellationToken) ?? ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
    }

    private static IResourceControlPlane CreateFailingPlane(string contributorName, Exception exception)
    {
        IResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddHealthContributor(new CallbackContributor(contributorName, _ => ValueTask.FromException<HealthContribution>(exception)));
        return plane;
    }

    private static bool IsContributorFailure(EventWrittenEventArgs item, string contributorName)
        => item.EventName == "ContributorFailed" && Equals(item.Payload![0], contributorName);

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new InvalidOperationException("exception-message-secret");
    }

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
