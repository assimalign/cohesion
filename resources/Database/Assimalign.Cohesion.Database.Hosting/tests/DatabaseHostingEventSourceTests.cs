using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Hosting.Internal;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Hosting.Resources;

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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: Should report build start and stop once for a real application")]
    public async Task Application_StartedAndStopped_ShouldReportEachTransitionOnce()
    {
        // Arrange
        using var recorder = new HostingEventRecorder(EventLevel.Informational);
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine("lifecycle", _ => SqlDatabaseEngine.Create("lifecycle", new SqlDatabaseEngineOptions()));
        builder.AddService(new RecordingService([]));
        await using var application = builder.Build();

        // Act
        await ((IHost)application).StopAsync(CancellationToken.None);
        await ((IHost)application).StartAsync(CancellationToken.None);
        await ((IHost)application).StartAsync(CancellationToken.None);
        await ((IHost)application).StopAsync(CancellationToken.None);
        await ((IHost)application).StopAsync(CancellationToken.None);

        // Assert
        string hostId = application.Context.HostId.ToString();
        Event(recorder, 7, "ApplicationBuilt", "hostId", "engines", "servers", "services").Payload.ShouldBe([hostId, 1, 0, 1]);
        Event(recorder, 8, "ApplicationStarting", "hostId").Payload.ShouldBe([hostId]);
        var started = Event(recorder, 9, "ApplicationStarted", "hostId", "durationMilliseconds");
        started.Payload![0].ShouldBe(hostId);
        ((double)started.Payload[1]!).ShouldBeGreaterThanOrEqualTo(0);
        var stopped = Event(recorder, 11, "ApplicationStopped", "hostId", "durationMilliseconds");
        stopped.Payload![0].ShouldBe(hostId);
        ((double)stopped.Payload[1]!).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - ApplicationStartFailed: Should report the original failure once after rollback")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Application_FailedStart_ShouldReportOnceAfterRollback(bool run)
    {
        // Arrange
        var order = new List<string>();
        var failure = new IOException("startup probe");
        await using var engine = new RecordingEngine();
        engine.AddServer(owner => new RecordingServer(order, "first", owner));
        engine.AddServer(owner => new RecordingServer(order, "failing", owner) { StartException = failure });
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddService(new RecordingService(order));
        builder.AddEngine(engine);
        await using var application = builder.Build();
        using var recorder = new HostingEventRecorder(EventLevel.Informational)
        {
            OnWritten = entry =>
            {
                if (entry.EventId == 10) { order.Add("failure-event"); }
            },
        };

        // Act
        var actual = await Should.ThrowAsync<IOException>(async () =>
        {
            if (run) { await ((IHost)application).RunAsync(CancellationToken.None); }
            else { await ((IHost)application).StartAsync(CancellationToken.None); }
        });

        // Assert
        actual.ShouldBeSameAs(failure);
        order.ShouldBe(["service:start", "first:start", "failing:start", "first:stop", "service:stop", "failure-event"]);
        var failed = Event(recorder, 10, "ApplicationStartFailed", "hostId", "exceptionType", "exceptionMessage");
        failed.Level.ShouldBe(EventLevel.Error);
        failed.Payload.ShouldBe([application.Context.HostId.ToString(), typeof(IOException).FullName, "startup probe"]);
        Event(recorder, 8, "ApplicationStarting", "hostId").Payload.ShouldBe([application.Context.HostId.ToString()]);
        recorder.Events.ShouldNotContain(entry => entry.EventId == 9);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - ApplicationDisposeFailed: Should report aggregate disposal failure only once")]
    public async Task DisposeAsync_FailedOwnedEngine_ShouldReportOnce()
    {
        // Arrange
        var engine = new RecordingEngine { DisposeException = new IOException("dispose probe") };
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine.Name, _ => engine);
        var application = builder.Build();
        using var recorder = new HostingEventRecorder(EventLevel.Error);

        // Act
        await Should.ThrowAsync<AggregateException>(async () => await ((IAsyncDisposable)application).DisposeAsync());
        await Should.ThrowAsync<AggregateException>(async () => await ((IAsyncDisposable)application).DisposeAsync());

        // Assert
        engine.DisposeCount.ShouldBe(1);
        var failed = Event(recorder, 12, "ApplicationDisposeFailed", "hostId", "failureCount", "exceptionType", "exceptionMessage");
        failed.Payload![0].ShouldBe(application.Context.HostId.ToString());
        failed.Payload[1].ShouldBe(1);
        failed.Payload[2].ShouldBe(typeof(AggregateException).FullName);
        failed.Payload[3].ShouldBeOfType<string>().ShouldContain("dispose probe", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Resource commands: Should report handled and rejected operations once with bounded identifiers")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceCommand_HandledOrRejected_ShouldReportOnce(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("commands", new SqlDatabaseEngineOptions());
        var builder = DatabaseApplication.CreateBuilder();
        builder.AddEngine(engine);
        await using var application = builder.Build();
        var handler = new DatabaseResourceCommandHandler("database.add-database", application.Context);
        var command = new ResourceCommand("id", handler.Kind, "owner", "commands/app", Encoding.UTF8.GetBytes("""{"database":"app","engine":"commands"}"""));
        if (delete)
        {
            await handler.ExecuteAsync(command, CancellationToken.None);
        }
        using var recorder = new HostingEventRecorder(EventLevel.Informational);

        // Act
        if (delete)
        {
            await handler.DeleteAsync(command, CancellationToken.None);
        }
        else
        {
            await handler.ExecuteAsync(command, CancellationToken.None);
        }
        var refused = new DatabaseResourceCommandHandler("database.add-principal", application.Context);
        var rejectedCommand = command with
        {
            Kind = refused.Kind,
            Key = new string('k', 300),
            Payload = Encoding.UTF8.GetBytes("""{"database":"app","name":"private-payload"}"""),
        };
        await Should.ThrowAsync<ResourceCommandRejectedException>(async () =>
        {
            if (delete) { await refused.DeleteAsync(rejectedCommand, CancellationToken.None); }
            else { await refused.ExecuteAsync(rejectedCommand, CancellationToken.None); }
        });

        // Assert
        string operation = delete ? "Delete" : "Execute";
        Event(recorder, 13, "ResourceCommandHandled", "kind", "key", "operation").Payload.ShouldBe([handler.Kind, command.Key, operation]);
        Event(recorder, 14, "ResourceCommandRejected", "kind", "key", "operation", "exceptionType").Payload
            .ShouldBe([refused.Kind, new string('k', 256) + "...", operation, typeof(ResourceCommandRejectedException).FullName]);
        recorder.Events.SelectMany(entry => entry.Payload ?? []).ShouldNotContain("private-payload");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - AdminEndpointFailed: Should report a real listener bind failure once")]
    public async Task AdminEndpoint_OccupiedPort_ShouldReportFailureOnce()
    {
        // Arrange
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)occupied.LocalEndpoint).Port}");
        using var scope = ResourceRuntime.CreateScope(new ResourceContext(endpoints: new Dictionary<string, Uri> { ["admin"] = endpoint }));
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions(), typeof(DatabaseHostingEventSourceTests).Assembly);
        var application = builder.Build();
        using var recorder = new HostingEventRecorder(EventLevel.Error);

        // Act: the background service may fail synchronously or after Start returned. Stop joins
        // its task in either case; disposal retains the existing failure behavior.
        try
        {
            try { await ((IHost)application).StartAsync(CancellationToken.None); }
            catch (Exception) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!recorder.Events.Any(entry => entry.EventId == 15))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        finally
        {
            try { await ((IAsyncDisposable)application).DisposeAsync(); }
            catch (AggregateException) { }
        }

        // Assert
        var failed = Event(recorder, 15, "AdminEndpointFailed", "endpoint", "operation", "exceptionType", "exceptionMessage");
        failed.Payload![0].ShouldBe(endpoint.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        failed.Payload[1].ShouldBe("Start");
        failed.Payload[2].ShouldBeOfType<string>().ShouldNotBeNullOrEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Disabled tracing: Should allocate and time nothing")]
    public async Task Writes_NoListener_ShouldAllocateAndTimeNothing()
    {
        // Arrange
        await using var application = DatabaseApplication.CreateBuilder().Build();
        var command = new ResourceCommand("id", "database.add-database", "owner", new string('k', 300), ReadOnlyMemory<byte>.Empty);
        var endpoint = new Uri("http://localhost:12345");
        var failure = new IOException("probe");
        DatabaseHostingEventSource.Log.IsEnabled().ShouldBeFalse();
        Write();

        // Act
        long allocated = long.MaxValue;
        long timestamp = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            timestamp |= Write();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert
        allocated.ShouldBe(0);
        timestamp.ShouldBe(0);

        long Write()
        {
            var log = DatabaseHostingEventSource.Log;
            long started = log.LifecycleTimestamp();
            for (int index = 0; index < 10_000; index++)
            {
                log.ApplicationBuilt(application.Context, 1);
                log.ApplicationStarting(application.Context);
                log.ApplicationStarted(application.Context, started);
                log.ApplicationStartFailed(application.Context, failure);
                log.ApplicationStopped(application.Context, started);
                log.ApplicationDisposeFailed(application.Context, 1, failure);
                log.ResourceCommandHandled(command, "Execute");
                log.ResourceCommandRejected(command, "Execute", failure);
                log.AdminEndpointFailed(endpoint, "Start", failure);
            }
            return started;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Hosting] - Disabled tracing: Real command dispatch should allocate only its core baseline")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceCommand_NoListener_ShouldAllocateOnlyTheCoreBaseline(bool delete)
    {
        // Arrange: the real handler parses and resolves an in-memory SQL engine on every call.
        // Repeated Execute deliveries retain the declared database; repeated Delete deliveries
        // find it already absent. Both are normal idempotent control-plane operations.
        await using var engine = SqlDatabaseEngine.Create("allocations", new SqlDatabaseEngineOptions());
        var builder = new DatabaseApplicationBuilder(new DatabaseApplicationOptions { ReopenOfflineDatabases = false });
        builder.AddEngine(engine);
        await using var application = builder.Build();
        var handler = new DatabaseResourceCommandHandler("database.add-database", application.Context);
        var command = new ResourceCommand("id", handler.Kind, "owner", "allocations/app",
            Encoding.UTF8.GetBytes("""{"database":"app","engine":"allocations"}"""));
        await handler.ExecuteAsync(command, CancellationToken.None);
        if (delete) { await handler.DeleteAsync(command, CancellationToken.None); }
        DatabaseHostingEventSource.Log.IsEnabled().ShouldBeFalse();
        Func<ValueTask<ReadOnlyMemory<byte>>> core = delete
            ? () => handler.DeleteCoreAsync(command, CancellationToken.None)
            : () => handler.ExecuteCoreAsync(command, CancellationToken.None);
        Func<ValueTask<ReadOnlyMemory<byte>>> dispatch = delete
            ? () => handler.DeleteAsync(command, CancellationToken.None)
            : () => handler.ExecuteAsync(command, CancellationToken.None);
        Measure(core, 100);
        Measure(dispatch, 100);
        const int iterations = 10_000;

        // Act
        long baseline = FewestBytes(core, iterations);
        long allocated = FewestBytes(dispatch, iterations);

        // Assert
        baseline.ShouldBeGreaterThan(0);
        allocated.ShouldBe(baseline);
        engine.TryGetDatabase("app", out _).ShouldBe(!delete);
        DatabaseHostingEventSource.Log.IsEnabled().ShouldBeFalse();
    }

    // DatabaseEventSourceTests.FewestBytes uses the same three-round minimum: incidental runtime
    // allocations can land on the thread, whereas a per-command regression affects every round.
    private static long FewestBytes(Func<ValueTask<ReadOnlyMemory<byte>>> execute, int iterations)
    {
        long fewest = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            fewest = Math.Min(fewest, Measure(execute, iterations));
        }
        return fewest;
    }

    private static long Measure(Func<ValueTask<ReadOnlyMemory<byte>>> execute, int iterations)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < iterations; index++)
        {
            var pending = execute();
            if (!pending.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException("The measured command did not complete synchronously.");
            }
            _ = pending.Result;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static EventWrittenEventArgs Event(HostingEventRecorder recorder, int id, string name, params string[] payloadNames)
    {
        recorder.Events.ShouldNotContain(entry => entry.EventId == 0);
        var written = recorder.Events.Where(entry => entry.EventId == id).ShouldHaveSingleItem();
        written.EventName.ShouldBe(name);
        written.PayloadNames.ShouldBe(payloadNames);
        return written;
    }

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
        var builder = SqlDatabaseEngine.CreateBuilder(engineName);
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new FailTwice(engineName + "/probe"));
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync(App);

        var options = new DatabaseApplicationOptions
        {
            ReopenInitialDelay = TimeSpan.FromMilliseconds(20),
            ReopenMaximumDelay = TimeSpan.FromMilliseconds(40),
        };
        var applicationBuilder = new DatabaseApplicationBuilder(options);
        applicationBuilder.AddEngine(engine);
        await using var application = applicationBuilder.Build();
        var service = application.Context.ReopenService.ShouldNotBeNull();
        int attempts = 0;
        service.Reopen = async (target, name, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("Injected reopen failure");
            }

            return await target.OpenDatabaseAsync(name, token);
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
        var builder = SqlDatabaseEngine.CreateBuilder(engineName);
        builder.Options.WorkerFailureWindow = TimeSpan.FromTicks(1);
        builder.Options.WorkerFailureMinimumPasses = 2;
        builder.AddWorker(_ => worker = new FailTwice(engineName + "/probe"));
        await using var engine = builder.Build();
        await engine.CreateDatabaseAsync(App);
        var options = new DatabaseApplicationOptions
        {
            ReopenInitialDelay = TimeSpan.FromSeconds(2),
            ReopenMaximumDelay = TimeSpan.FromSeconds(4),
        };
        var applicationBuilder = new DatabaseApplicationBuilder(options);
        applicationBuilder.AddEngine(engine);
        await using var application = applicationBuilder.Build();
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

        public Action<EventWrittenEventArgs>? OnWritten { get; init; }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, DatabaseHostingEventSource.Log))
            {
                _events.Enqueue(eventData);
                OnWritten?.Invoke(eventData);
            }
        }
    }
}
