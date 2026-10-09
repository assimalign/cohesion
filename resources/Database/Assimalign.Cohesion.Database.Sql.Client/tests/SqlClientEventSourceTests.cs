using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Client.Internal;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

/// <summary>
/// Serializes the tests that observe the SQL client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(SqlClientEventSourceCollection), DisableParallelization = true)]
public class SqlClientEventSourceCollection
{
}

/// <summary>
/// The SQL client's event source against the repository's EventSource convention: each command
/// starts and stops, or fails, once; an observer hook that throws is reported once; and no
/// statement text or parameter value is written.
/// </summary>
[Collection(nameof(SqlClientEventSourceCollection))]
public sealed class SqlClientEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should be named for its assembly")]
    public void GetName_SqlClientEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(SqlClientEventSource));

        // Assert
        name.ShouldBe(typeof(SqlClientEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Sql.Client");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(SqlClientEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Sql.Client", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should report each command and each throwing observer hook once, with its declared payload")]
    public async Task Commands_SucceedAndFail_ShouldReportEachOnce()
    {
        // Arrange: an observer whose every hook throws.
        await using var harness = await SqlClientTestHarness.StartAsync(observer: new ThrowingObserver());
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        using var recorder = new SqlClientEventRecorder();

        // Act
        SqlResultSet rows = await connection.QueryAsync(
            "SELECT name FROM users WHERE id = @id",
            new Dictionary<string, object?> { ["id"] = 1 },
            SqlClientTestHarness.Timeout());
        var failure = await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT id FROM missing_table", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert: the throwing hooks changed no outcome.
        rows.Count.ShouldBe(1);
        failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);

        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], SqlClientTestHarness.DatabaseName)).ToArray();
        events.Select(e => (e.EventName, Detail: e.EventName == "ObserverFailed" ? (string)e.Payload![1]! : string.Empty)).ShouldBe(
        [
            ("ObserverFailed", "OnExecuting"),
            ("CommandStart", string.Empty),
            ("CommandStop", string.Empty),
            ("ObserverFailed", "OnExecuted"),
            ("ObserverFailed", "OnExecuting"),
            ("CommandStart", string.Empty),
            ("CommandFailed", string.Empty),
            ("CommandStop", string.Empty),
            ("ObserverFailed", "OnFailed"),
        ]);

        var start = events[1];
        start.EventId.ShouldBe(1);
        start.Level.ShouldBe(EventLevel.Verbose);
        (start.Keywords & SqlClientEventSource.Keywords.Commands).ShouldBe(SqlClientEventSource.Keywords.Commands);
        start.PayloadNames.ShouldBe(["database", "parameterCount"]);
        start.Payload.ShouldBe([SqlClientTestHarness.DatabaseName, 1]);

        var stop = events[2];
        stop.EventId.ShouldBe(2);
        stop.Level.ShouldBe(EventLevel.Verbose);
        stop.PayloadNames.ShouldBe(["database", "status", "rowCount", "affectedCount", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Success");
        stop.Payload[2].ShouldBe(1L);
        stop.Payload[3].ShouldBe(-1L);
        ((double)stop.Payload[4]!).ShouldBeGreaterThanOrEqualTo(0d);

        var failed = events[6];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "errorKind", "code", "exceptionType", "durationMilliseconds"]);
        failed.Payload![1].ShouldBe(nameof(SqlClientErrorKind.ExecutionFailure));
        failed.Payload[2].ShouldBe(failure.Code.ToString());
        failed.Payload[3].ShouldBe(typeof(SqlClientException).FullName);

        // The failure's stop follows it, with Error and no counts.
        var failedStop = events[7];
        failedStop.EventId.ShouldBe(2);
        failedStop.Payload!.Take(4).ShouldBe([SqlClientTestHarness.DatabaseName, "Error", -1L, -1L]);

        var observerFailed = events[0];
        observerFailed.EventId.ShouldBe(4);
        observerFailed.Level.ShouldBe(EventLevel.Warning);
        observerFailed.PayloadNames.ShouldBe(["database", "callback", "exceptionType", "exceptionMessage"]);
        observerFailed.Payload.ShouldBe([SqlClientTestHarness.DatabaseName, "OnExecuting", typeof(InvalidOperationException).FullName, "OnExecuting failed"]);

        // No payload carries the statement text, a parameter value or the server's message.
        events.SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value => value.Contains("SELECT", StringComparison.Ordinal));
        events.SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value => value == failure.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should close a cancelled command with a Cancelled stop and no failure")]
    public async Task Command_Cancelled_ShouldWriteACancelledStop()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        using var recorder = new SqlClientEventRecorder();

        // Act: a token canceled before the command's exchange writes its first frame.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.QueryAsync("SELECT id FROM users", cancellationToken: new CancellationToken(canceled: true)));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], SqlClientTestHarness.DatabaseName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["CommandStart", "CommandStop"]);
        events[1].Payload!.Take(4).ShouldBe([SqlClientTestHarness.DatabaseName, "Cancelled", -1L, -1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should write no command start or stop without the Commands keyword")]
    public async Task Commands_WithoutCommandsKeyword_ShouldWriteNoStartOrStop()
    {
        // Arrange
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        using var recorder = new SqlClientEventRecorder((EventKeywords)0x2);

        // Act
        await connection.QueryAsync("SELECT id FROM users", cancellationToken: SqlClientTestHarness.Timeout());
        await Should.ThrowAsync<SqlClientException>(async () =>
            await connection.QueryAsync("SELECT id FROM missing_table", cancellationToken: SqlClientTestHarness.Timeout()));

        // Assert: the failure has no keyword, so it is still written.
        recorder.Events.Where(e => Equals(e.Payload?[0], SqlClientTestHarness.DatabaseName)).Select(e => e.EventName).ShouldBe(["CommandFailed"]);
    }

    /// <summary>
    /// An observer whose every hook throws.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Sql.Client] - SqlClientEventSource: Should write an uncoded failure with empty kind and code, and a stop only for a command whose start was written")]
    public async Task CommandEnded_UncodedFailure_ShouldWriteEmptyCodeAndStopOnlyWhenStarted()
    {
        // Arrange: a failure the client raises itself (an overlapping exchange) carries no wire code.
        await using var harness = await SqlClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(SqlClientTestHarness.Timeout());
        using var recorder = new SqlClientEventRecorder();
        var failure = new InvalidOperationException("An exchange is already active on this connection.");

        // Act: one command whose start a listener that attached late never saw, and one it saw.
        SqlClientEventSource.Log.CommandEnded(connection, startWritten: false, failure, -1, -1, Stopwatch.GetTimestamp());
        SqlClientEventSource.Log.CommandEnded(connection, startWritten: true, failure, -1, -1, Stopwatch.GetTimestamp());

        // Assert: the failure is written either way, the stop only after a written start.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], SqlClientTestHarness.DatabaseName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["CommandFailed", "CommandFailed", "CommandStop"]);
        events[0].Payload!.Take(4).ShouldBe([SqlClientTestHarness.DatabaseName, string.Empty, string.Empty, typeof(InvalidOperationException).FullName]);
        events[2].Payload!.Take(4).ShouldBe([SqlClientTestHarness.DatabaseName, "Error", -1L, -1L]);
    }

    private sealed class ThrowingObserver : SqlClientObserver
    {
        protected internal override void OnExecuting(string commandText, int parameterCount)
            => throw new InvalidOperationException("OnExecuting failed");

        protected internal override void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
            => throw new InvalidOperationException("OnExecuted failed");

        protected internal override void OnFailed(string commandText, SqlClientException exception, TimeSpan elapsed)
            => throw new InvalidOperationException("OnFailed failed");
    }

    /// <summary>
    /// Records the events the SQL client's event source writes.
    /// </summary>
    private sealed class SqlClientEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public SqlClientEventRecorder(EventKeywords keywords = EventKeywords.All)
        {
            EnableEvents(SqlClientEventSource.Log, EventLevel.Verbose, keywords);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, SqlClientEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
