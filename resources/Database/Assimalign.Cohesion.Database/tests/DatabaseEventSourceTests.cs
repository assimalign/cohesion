using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Internal;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// Serializes the tests that observe the Database event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseEventSourceCollection), DisableParallelization = true)]
public class DatabaseEventSourceCollection
{
}

/// <summary>
/// The Database root's event source against the repository's EventSource convention, and the
/// worker failures and recoveries it reports (#1268): one event per failure, one per recovery.
/// </summary>
/// <remarks>
/// The source declares no counters: a worker's counts are on the worker itself
/// (<see cref="DatabaseEngineWorker.FailureCount"/>).
/// </remarks>
[Collection(nameof(DatabaseEventSourceCollection))]
public sealed class DatabaseEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should be named for its assembly")]
    public void GetName_DatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(DatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database");
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should report each worker failure and each recovery once with its declared payload")]
    public void WorkerFailuresAndRecoveries_ShouldBeReportedOnceEach()
    {
        // Arrange: database a fails on passes 1 and 2 and recovers on pass 3; pass 4 throws as a
        // whole, and pass 5 runs to its end.
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        var worker = new ScriptedWorker((self, pass) =>
        {
            if (pass == 4)
            {
                throw new InvalidOperationException("the pass failed");
            }

            self.Begin("a").ShouldBeTrue();
            if (pass <= 2)
            {
                self.Fail("a", new InvalidOperationException($"database a failed on pass {pass}"), TimeSpan.Zero);
            }
        }, name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Informational);

        // Act
        for (int pass = 1; pass <= 5; pass++)
        {
            worker.RunIteration(CancellationToken.None);
        }

        // Assert
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], name)).ToArray();
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        events.Select(e => (e.EventName, Database: (string)e.Payload![2]!)).ShouldBe(
        [
            ("WorkerFailed", "a"),
            ("WorkerFailed", "a"),
            ("WorkerRecovered", "a"),
            ("WorkerFailed", string.Empty),
            ("WorkerRecovered", string.Empty),
        ]);

        var failed = events[1];
        failed.EventId.ShouldBe(1);
        failed.Level.ShouldBe(EventLevel.Warning);
        failed.PayloadNames.ShouldBe(["workerName", "workerKind", "database", "exceptionType", "exceptionMessage", "consecutiveFailures"]);
        failed.Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), "a", typeof(InvalidOperationException).FullName, "database a failed on pass 2", 2]);

        var recovered = events[2];
        recovered.EventId.ShouldBe(2);
        recovered.Level.ShouldBe(EventLevel.Informational);
        recovered.PayloadNames.ShouldBe(["workerName", "workerKind", "database", "failures"]);
        recovered.Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), "a", 2]);

        events[3].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), string.Empty, typeof(InvalidOperationException).FullName, "the pass failed", 1]);
        events[4].Payload.ShouldBe([name, nameof(DatabaseEngineWorkerKind.Checkpoint), string.Empty, 1]);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - DatabaseEventSource: Should write nothing below its enabled level")]
    public void WorkerRecovered_WarningListener_ShouldNotBeWritten()
    {
        // Arrange
        string name = "event-source-" + Guid.NewGuid().ToString("N");
        var worker = new ScriptedWorker((self, pass) =>
        {
            self.Begin("a").ShouldBeTrue();
            if (pass == 1)
            {
                self.Fail("a", new InvalidOperationException("failed"), TimeSpan.Zero);
            }
        }, name: name);
        using var recorder = new DatabaseEventRecorder(EventLevel.Warning);

        // Act
        worker.RunIteration(CancellationToken.None);
        worker.RunIteration(CancellationToken.None);

        // Assert: the failure (a warning) only.
        recorder.Events.Where(e => Equals(e.Payload?[0], name)).Select(e => e.EventName).ShouldBe(["WorkerFailed"]);
    }

    /// <summary>
    /// Records the events the Database event source writes.
    /// </summary>
    private sealed class DatabaseEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public DatabaseEventRecorder(EventLevel level)
        {
            EnableEvents(DatabaseEventSource.Log, level, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, DatabaseEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
