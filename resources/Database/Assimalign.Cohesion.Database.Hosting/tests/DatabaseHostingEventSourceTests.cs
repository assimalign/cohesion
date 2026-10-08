using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// Serializes the tests that observe the Database hosting event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(DatabaseHostingEventSourceCollection), DisableParallelization = true)]
public class DatabaseHostingEventSourceCollection
{
}

/// <summary>
/// The Database hosting module's event source against the repository's EventSource convention, and
/// the reopen of an offline database it reports (owner decision 22): the finding, each attempt and
/// each outcome once, with its declared payload.
/// </summary>
/// <remarks>
/// The source declares no counters: the reopen attempts are on the application's health.
/// </remarks>
[Collection(nameof(DatabaseHostingEventSourceCollection))]
public sealed class DatabaseHostingEventSourceTests
{
    private const string App = "app";

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - DatabaseHostingEventSource: Should be named for its assembly")]
    public void GetName_DatabaseHostingEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(DatabaseHostingEventSource));

        // Assert
        name.ShouldBe(typeof(DatabaseHostingEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Hosting");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - DatabaseHostingEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(DatabaseHostingEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Hosting", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - DatabaseHostingEventSource: Should report the finding, each reopen attempt and each outcome once")]
    public async Task Reopen_ShouldReportEachStepOnceWithItsPayload()
    {
        // Arrange: a real SQL engine whose database a failing checkpoint worker takes offline after
        // two failed passes a millisecond apart (a window of a tick, owner decision 42), and a
        // reopen that fails once, then runs the engine's own reopen.
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        FailTwice? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.EngineName = engineName;
        builder.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new FailTwice(engineName + "/probe"));
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync(App);

        var options = new DatabaseApplicationOptions
        {
            ReopenInitialDelay = TimeSpan.FromMilliseconds(20),
            ReopenMaximumDelay = TimeSpan.FromMilliseconds(40),
        };
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        var service = application.Context.ReopenService.ShouldNotBeNull();
        int attempts = 0;
        service.Reopen = async (target, name, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("Injected reopen failure");
            }

            await target.OpenDatabaseAsync(name, token);
        };
        using var recorder = new HostingEventRecorder(EventLevel.Informational);

        // Act: the engine takes the database offline on a thread-pool thread, a moment after the
        // second failed pass.
        worker.ShouldNotBeNull().RunFailingPasses();
        var watch = Stopwatch.StartNew();
        while (engine.OfflineDatabases.Count == 0)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        await ((IHost)application).StartAsync(CancellationToken.None);
        while (engine.OfflineDatabases.Count > 0 || service.GetPendingReopens().Count > 0)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        await ((IHost)application).StopAsync(CancellationToken.None);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?.FirstOrDefault(), engineName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(
        [
            "OfflineDatabaseFound",
            "ReopenAttempted",
            "ReopenFailed",
            "ReopenAttempted",
            "ReopenSucceeded",
        ]);

        var found = events[0];
        found.EventId.ShouldBe(1);
        found.Level.ShouldBe(EventLevel.Warning);
        found.PayloadNames.ShouldBe(["engineName", "database", "cause", "delayMilliseconds"]);
        var foundPayload = found.Payload.ShouldNotBeNull();
        foundPayload[1].ShouldBe(App);
        foundPayload[2].ShouldBe("CheckpointFailures");
        ((long)foundPayload[3]!).ShouldBeInRange(10L, 20L);

        events[1].EventId.ShouldBe(2);
        events[1].PayloadNames.ShouldBe(["engineName", "database", "attempt"]);
        events[1].Payload.ShouldBe([engineName, App, 1]);

        var failed = events[2];
        failed.EventId.ShouldBe(4);
        failed.Level.ShouldBe(EventLevel.Warning);
        failed.PayloadNames.ShouldBe(["engineName", "database", "attempt", "exceptionType", "exceptionMessage", "retryMilliseconds"]);
        var failedPayload = failed.Payload.ShouldNotBeNull();
        failedPayload.Take(5).ShouldBe([engineName, App, 1, typeof(IOException).FullName, "Injected reopen failure"]);
        ((long)failedPayload[5]!).ShouldBeInRange(20L, 40L);

        events[3].Payload.ShouldBe([engineName, App, 2]);

        var succeeded = events[4];
        succeeded.EventId.ShouldBe(3);
        succeeded.Level.ShouldBe(EventLevel.Informational);
        succeeded.PayloadNames.ShouldBe(["engineName", "database", "attempts"]);
        succeeded.Payload.ShouldBe([engineName, App, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - DatabaseHostingEventSource: Should report a database let go without a reopen")]
    public async Task Reopen_DatabaseDropped_ShouldReportItAbandoned()
    {
        // Arrange: the first reopen waits a second or two; the database is dropped meanwhile.
        string engineName = "event-source-" + Guid.NewGuid().ToString("N");
        FailTwice? worker = null;
        var builder = SqlDatabaseEngine.CreateBuilder();
        builder.EngineName = engineName;
        builder.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new FailTwice(engineName + "/probe"));
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync(App);
        var options = new DatabaseApplicationOptions
        {
            ReopenInitialDelay = TimeSpan.FromSeconds(2),
            ReopenMaximumDelay = TimeSpan.FromSeconds(4),
        };
        options.Engines.Add(engine);
        await using var application = new DatabaseApplication(options);
        var service = application.Context.ReopenService.ShouldNotBeNull();
        using var recorder = new HostingEventRecorder(EventLevel.Informational);

        // Act
        worker.ShouldNotBeNull().RunFailingPasses();
        var watch = Stopwatch.StartNew();
        while (engine.OfflineDatabases.Count == 0)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        await ((IHost)application).StartAsync(CancellationToken.None);
        while (service.GetPendingReopens().Count == 0)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        await engine.DropDatabaseAsync(App);
        while (service.GetPendingReopens().Count > 0)
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            await Task.Delay(10);
        }

        await ((IHost)application).StopAsync(CancellationToken.None);

        // Assert
        var events = recorder.Events.Where(e => Equals(e.Payload?.FirstOrDefault(), engineName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["OfflineDatabaseFound", "ReopenAbandoned"]);
        var abandoned = events[1];
        abandoned.EventId.ShouldBe(5);
        abandoned.Level.ShouldBe(EventLevel.Informational);
        abandoned.PayloadNames.ShouldBe(["engineName", "database", "reason"]);
        abandoned.Payload![1].ShouldBe(App);
        abandoned.Payload[2].ShouldBeOfType<string>().ShouldContain("no longer lists it offline");
    }

    /// <summary>
    /// A checkpoint worker whose first two passes fail on the database, with no backoff, and whose
    /// later passes finish. Its one-hour interval keeps the engine's pump from running a pass the
    /// test did not ask for.
    /// </summary>
    private sealed class FailTwice : DatabaseEngineWorker
    {
        private int _passes;

        public FailTwice(string name)
            : base(name, DatabaseEngineWorkerKind.Checkpoint, TimeSpan.FromHours(1))
        {
        }

        /// <summary>
        /// Runs the two failing passes, a millisecond apart: past an engine window of a tick, and
        /// at a minimum of two passes, the second gives up on the database (owner decision 42).
        /// </summary>
        public void RunFailingPasses()
        {
            RunIteration(CancellationToken.None);
            Thread.Sleep(1);
            RunIteration(CancellationToken.None);
        }

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (BeginDatabase(App) && Interlocked.Increment(ref _passes) <= 2)
            {
                ReportFailure(App, new IOException("Injected checkpoint failure"), TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Records the events the Database hosting event source writes.
    /// </summary>
    private sealed class HostingEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public HostingEventRecorder(EventLevel level)
        {
            EnableEvents(DatabaseHostingEventSource.Log, level, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, DatabaseHostingEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
