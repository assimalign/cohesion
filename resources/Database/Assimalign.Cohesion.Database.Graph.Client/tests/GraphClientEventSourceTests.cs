using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Client.Internal;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

/// <summary>
/// Serializes the tests that observe the graph client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(GraphClientEventSourceCollection), DisableParallelization = true)]
public class GraphClientEventSourceCollection
{
}

/// <summary>
/// The graph client's event source against the repository's EventSource convention: each query of
/// each public member starts and stops, or fails, once, and no statement text is written.
/// </summary>
[Collection(nameof(GraphClientEventSourceCollection))]
public sealed class GraphClientEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should be named for its assembly")]
    public void GetName_GraphClientEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(GraphClientEventSource));

        // Assert
        name.ShouldBe(typeof(GraphClientEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Graph.Client");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(GraphClientEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Graph.Client", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should report each query of each member once, with its declared payload")]
    public async Task Queries_EachMember_ShouldReportEachOnce()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();

        // Act
        long created = await connection.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);
        GraphResultSet names = await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token);
        var paths = new List<GraphPath>();
        await foreach (GraphPath path in connection.QueryPathsAsync("MATCH (a:Person) RETURN a AS person", cancellationToken: harness.Token))
        {
            paths.Add(path);
        }

        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("MATCH (n:Person RETURN n", cancellationToken: harness.Token));

        // Assert
        created.ShouldBe(2L);
        names.Count.ShouldBe(2);
        paths.Count.ShouldBe(2);

        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph")).ToArray();
        events.Select(e => (e.EventName, Operation: (string)e.Payload![1]!)).ShouldBe(
        [
            ("QueryStart", "Execute"),
            ("QueryStop", "Execute"),
            ("QueryStart", "Query"),
            ("QueryStop", "Query"),
            ("QueryStart", "QueryPaths"),
            ("QueryStop", "QueryPaths"),
            ("QueryStart", "Query"),
            ("QueryFailed", "Query"),
            ("QueryStop", "Query"),
        ]);

        var start = events[0];
        start.EventId.ShouldBe(1);
        start.Level.ShouldBe(EventLevel.Verbose);
        (start.Keywords & GraphClientEventSource.Keywords.Queries).ShouldBe(GraphClientEventSource.Keywords.Queries);
        start.PayloadNames.ShouldBe(["database", "operation"]);

        var stop = events[3];
        stop.EventId.ShouldBe(2);
        stop.Level.ShouldBe(EventLevel.Verbose);
        stop.PayloadNames.ShouldBe(["database", "operation", "status", "rowCount", "durationMilliseconds"]);
        stop.Payload![2].ShouldBe("Success");
        stop.Payload[3].ShouldBe(2L);
        ((double)stop.Payload[4]!).ShouldBeGreaterThan(0d);
        events[5].Payload![2].ShouldBe("Success");
        events[5].Payload![3].ShouldBe(2L, "A path query's row count is its paths.");

        var failed = events[7];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "operation", "code", "exceptionType", "durationMilliseconds"]);
        failed.Payload![2].ShouldBe(failure.Code.ToString());
        failed.Payload[3].ShouldBe(typeof(GraphClientException).FullName);
        events[8].Payload!.Take(4).ShouldBe(["graph", "Query", "Error", -1L]);

        // No event carries the statement text or the server's message, which quotes it.
        events.SelectMany(e => e.Payload!).OfType<string>()
            .ShouldNotContain(value => value.Contains("MATCH", StringComparison.Ordinal) || value.Contains("CREATE", StringComparison.Ordinal) || value == failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should close a path query its caller stopped reading with a Cancelled stop and the paths read")]
    public async Task QueryPathsAsync_CallerStopsEarly_ShouldWriteACancelledStop()
    {
        // Arrange: two paths, of which the caller reads one.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var setup = await harness.Client.ConnectAsync(harness.Token);
        await setup.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);
        await setup.DisposeAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();

        // Act
        await foreach (GraphPath path in connection.QueryPathsAsync("MATCH (a:Person) RETURN a AS person", cancellationToken: harness.Token))
        {
            break;
        }

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph") && Equals(e.Payload?[1], "QueryPaths")).ToArray();
        events.Select(e => e.EventName).ShouldBe(["QueryStart", "QueryStop"]);
        events[1].Payload!.Take(4).ShouldBe(["graph", "QueryPaths", "Cancelled", 1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should close a cancelled query with a Cancelled stop and no failure")]
    public async Task QueryAsync_Cancelled_ShouldWriteACancelledStop()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();

        // Act: a token canceled before the exchange writes its first frame.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: new System.Threading.CancellationToken(canceled: true)));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph")).ToArray();
        events.Select(e => e.EventName).ShouldBe(["QueryStart", "QueryStop"]);
        events[1].Payload!.Take(4).ShouldBe(["graph", "Query", "Cancelled", -1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should report a path query the server refused with its code, then an Error stop with no paths")]
    public async Task QueryPathsAsync_ServerRefusesStatement_ShouldWriteFailedThenErrorStop()
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();

        // Act: a statement that does not parse.
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
        {
            await foreach (GraphPath path in connection.QueryPathsAsync("MATCH (a:Person RETURN a AS person", cancellationToken: harness.Token))
            {
            }
        });

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph") && Equals(e.Payload?[1], "QueryPaths")).ToArray();
        events.Select(e => e.EventName).ShouldBe(["QueryStart", "QueryFailed", "QueryStop"]);
        events[1].Payload![2].ShouldBe(failure.Code.ToString());
        events[1].Payload![2].ShouldBe("ParseFailure");
        events[1].Payload![3].ShouldBe(typeof(GraphClientException).FullName);
        events[2].Payload!.Take(4).ShouldBe(["graph", "QueryPaths", "Error", 0L]);
        events.SelectMany(e => e.Payload!).OfType<string>()
            .ShouldNotContain(value => value.Contains("MATCH", StringComparison.Ordinal) || value == failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should report a failure that carries no wire code with an empty code, then an Error stop")]
    public async Task QueryAsync_AnotherExchangeActive_ShouldWriteAnUncodedFailureThenErrorStop()
    {
        // Arrange: a path query left open after its first path holds the connection's one exchange.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var setup = await harness.Client.ConnectAsync(harness.Token);
        await setup.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);
        await setup.DisposeAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();
        var paths = connection.QueryPathsAsync("MATCH (a:Person) RETURN a AS person", cancellationToken: harness.Token).GetAsyncEnumerator(harness.Token);

        // Act
        InvalidOperationException overlapping;
        try
        {
            (await paths.MoveNextAsync()).ShouldBeTrue();
            overlapping = await Should.ThrowAsync<InvalidOperationException>(async () =>
                await connection.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token));
        }
        finally
        {
            await paths.DisposeAsync();
        }

        // Assert: the client's own refusal has no wire code, so the code is empty.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph") && Equals(e.Payload?[1], "Query")).ToArray();
        events.Select(e => e.EventName).ShouldBe(["QueryStart", "QueryFailed", "QueryStop"]);
        events[1].Payload![2].ShouldBe(string.Empty);
        events[1].Payload![3].ShouldBe(typeof(InvalidOperationException).FullName);
        events[1].Payload!.ShouldNotContain(overlapping.Message);
        events[2].Payload!.Take(4).ShouldBe(["graph", "Query", "Error", -1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should write a failure whose start was not written, but no QueryStop for it")]
    public async Task QueryEnded_StartNotWritten_ShouldWriteTheFailureButNoStop()
    {
        // Arrange: a query whose start a listener that attached late never saw, beside one it saw.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        using var recorder = new GraphClientEventRecorder();
        var failure = new InvalidOperationException("An exchange is already active on this connection.");

        // Act
        GraphClientEventSource.Log.QueryEnded(connection, "Query", startWritten: false, failure, -1, System.Diagnostics.Stopwatch.GetTimestamp());
        GraphClientEventSource.Log.QueryEnded(connection, "Query", startWritten: true, failure, -1, System.Diagnostics.Stopwatch.GetTimestamp());

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        recorder.Events.Where(e => Equals(e.Payload?[0], "graph")).Select(e => e.EventName)
            .ShouldBe(["QueryFailed", "QueryFailed", "QueryStop"]);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - GraphClientEventSource: Should close a path query's activity with its stop, whether its caller reads every path or stops early")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryPathsAsync_ActivityTracking_ShouldCloseTheStartsActivity(bool stopEarly)
    {
        // Arrange: activity tracking on. A path query spans its caller's MoveNextAsync calls, each
        // on the caller's own execution context, so its stop must be written in its start's.
        await using var harness = await GraphClientTestHarness.StartAsync();
        await using var setup = await harness.Client.ConnectAsync(harness.Token);
        await setup.ExecuteAsync("CREATE (:Person {name: 'Alice'}), (:Person {name: 'Bob'})", cancellationToken: harness.Token);
        await setup.DisposeAsync();
        using var recorder = new GraphClientActivityRecorder();

        // Act: a path query, then a scalar query whose pair runs on one flow, for comparison. A path
        // query stopped early discards its connection, so the scalar query rents another once the
        // first is returned.
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            await foreach (GraphPath path in connection.QueryPathsAsync("MATCH (a:Person) RETURN a AS person", cancellationToken: harness.Token))
            {
                if (stopEarly)
                {
                    break;
                }
            }
        }

        await using (var scalar = await harness.Client.ConnectAsync(harness.Token))
        {
            await scalar.QueryAsync("MATCH (n:Person) RETURN n.name", cancellationToken: harness.Token);
        }

        // Assert
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], "graph")).ToArray();
        events.Select(e => (e.EventName, Operation: (string)e.Payload![1]!)).ShouldBe(
        [
            ("QueryStart", "QueryPaths"),
            ("QueryStop", "QueryPaths"),
            ("QueryStart", "Query"),
            ("QueryStop", "Query"),
        ]);
        events[1].Payload![2].ShouldBe(stopEarly ? "Cancelled" : "Success");
        events[0].ActivityId.ShouldNotBe(Guid.Empty, "Activity tracking gave the path query no activity id.");
        events[1].ActivityId.ShouldBe(events[0].ActivityId, "The path query's stop did not close its start's activity.");
        events[2].ActivityId.ShouldNotBe(Guid.Empty);
        events[3].ActivityId.ShouldBe(events[2].ActivityId);
        events[2].ActivityId.ShouldNotBe(events[0].ActivityId);
    }

    /// <summary>
    /// Records the events the graph client's event source writes.
    /// </summary>
    private sealed class GraphClientEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public GraphClientEventRecorder()
        {
            EnableEvents(GraphClientEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, GraphClientEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }

    /// <summary>
    /// Turns on the runtime's task-flow activity ids and records the graph client's events with the
    /// activity id each was written under.
    /// </summary>
    private sealed class GraphClientActivityRecorder : EventListener
    {
        private const string TaskSource = "System.Threading.Tasks.TplEventSource";

        // Compared by name: the source may be created while this listener exists, before its Log
        // field is assigned.
        private const string GraphClientSource = "Assimalign.Cohesion.Database.Graph.Client";

        // TplEventSource's TasksFlowActivityIds keyword: it turns on EventSource's activity tracking.
        private const EventKeywords TasksFlowActivityIds = (EventKeywords)0x80;

        // Initialized before the base constructor calls OnEventSourceCreated for existing sources.
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == TaskSource)
            {
                EnableEvents(eventSource, EventLevel.Informational, TasksFlowActivityIds);
            }
            else if (eventSource.Name == GraphClientSource)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == GraphClientSource)
            {
                // Read on the writing thread: the property is computed from the current thread when
                // first read, and cached.
                _ = eventData.ActivityId;
                _events.Enqueue(eventData);
            }
        }
    }
}
