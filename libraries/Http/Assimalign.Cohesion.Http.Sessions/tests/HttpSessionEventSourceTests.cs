using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Internal;
using Assimalign.Cohesion.Testing;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

[CollectionDefinition("Http session events", DisableParallelization = true)]
public sealed class HttpSessionEventSourceCollection { }

[Collection("Http session events")]
public sealed class HttpSessionEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - EventSource: Name matches assembly")]
    public void GetName_Source_ShouldMatchAssembly()
    {
        // Arrange / Act / Assert
        EventSource.GetName(typeof(HttpSessionEventSource)).ShouldBe("Assimalign.Cohesion.Http.Sessions");
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - EventSource: Strict manifest succeeds")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only manifest verification.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111", Justification = "Test-only manifest verification.")]
    public void GenerateManifest_Source_ShouldBeStrict()
    {
        // Arrange / Act / Assert
        EventSource.GenerateManifest(typeof(HttpSessionEventSource), null, EventManifestOptions.Strict).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Http.Sessions] - Store failure: Reports type once and preserves failure")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_FailingStore_ShouldReportWithoutSessionData(bool commit)
    {
        // Arrange — allow the process-wide throttle from earlier tests to expire.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        HttpSessionEventSource source = HttpSessionEventSource.Log;
        long before = commit ? source.CommitFailures : source.LoadFailures;
        using EventSourceRecorder recorder = new(source, EventLevel.Verbose);
        IOException failure = new("secret-session-id secret-session-data");
        FailingStore store = new(failure);
        IHttpStoredSession session = store.CreateSession("secret-session-id", TimeSpan.FromMinutes(20));
        session.SetString("secret-key", "secret-session-data");

        // Act
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IOException caught = await Should.ThrowAsync<IOException>(() => commit
                ? session.CommitAsync(timeout.Token)
                : session.LoadAsync(timeout.Token));
            caught.ShouldBeSameAs(failure);
        }

        // Assert
        (commit ? source.CommitFailures : source.LoadFailures).ShouldBe(before + 3);
        EventWrittenEventArgs entry = recorder.Events.Single(e => e.EventId == (commit ? 2 : 1));
        entry.Level.ShouldBe(EventLevel.Error);
        entry.PayloadNames!.ToArray().ShouldBe(new[] { "exceptionType", "suppressedCount" });
        entry.Payload![0].ShouldBe(typeof(IOException).FullName);
        session.IsModified.ShouldBeTrue();
        session.IsAvailable.ShouldBeFalse();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
        recorder.Events.SelectMany(e => e.Payload ?? []).OfType<string>()
            .ShouldNotContain(value => value.Contains("secret", StringComparison.Ordinal));

        // The next emitted failure accounts for the two suppressed attempts exactly.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), timeout.Token);
        await Should.ThrowAsync<IOException>(() => commit
            ? session.CommitAsync(timeout.Token)
            : session.LoadAsync(timeout.Token));
        recorder.Events.Last().Payload![1].ShouldBe(2);
        (commit ? source.CommitFailures : source.LoadFailures).ShouldBe(before + 4);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - EventSource: Counters publish")]
    public async Task EnableCounters_Source_ShouldPublishEveryCounter()
    {
        // Arrange
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using EventSourceRecorder recorder = new(HttpSessionEventSource.Log, EventLevel.Verbose, TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await recorder.WaitForCountersAsync(["store-load-failures", "store-commit-failures"], timeout.Token);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Store cancellation: Requested cancellation is not failure")]
    public async Task LoadAsync_RequestedCancellation_ShouldNotCountFailure()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        IHttpStoredSession session = new FailingStore(new OperationCanceledException(cancellation.Token))
            .CreateSession("id", TimeSpan.FromMinutes(20));
        long before = HttpSessionEventSource.Log.LoadFailures;

        // Act
        await Should.ThrowAsync<OperationCanceledException>(() => session.LoadAsync(cancellation.Token));

        // Assert
        HttpSessionEventSource.Log.LoadFailures.ShouldBe(before);
    }

    private sealed class FailingStore : IHttpSessionStore
    {
        private readonly Exception _failure;
        public FailingStore(Exception failure) => _failure = failure;
        public ValueTask<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default) => throw _failure;
        public ValueTask SetAsync(string sessionId, byte[] payload, TimeSpan idleTimeout, CancellationToken cancellationToken = default) => throw _failure;
        public ValueTask RefreshAsync(string sessionId, TimeSpan idleTimeout, CancellationToken cancellationToken = default) => throw _failure;
        public ValueTask RemoveAsync(string sessionId, CancellationToken cancellationToken = default) => throw _failure;
    }
}
