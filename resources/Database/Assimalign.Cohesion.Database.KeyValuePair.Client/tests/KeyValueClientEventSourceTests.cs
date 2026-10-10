using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Client.Internal;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client.Tests;

/// <summary>
/// Serializes the tests that observe the key-value client's event source: the source is process-wide.
/// </summary>
[CollectionDefinition(nameof(KeyValueClientEventSourceCollection), DisableParallelization = true)]
public class KeyValueClientEventSourceCollection
{
}

/// <summary>
/// The key-value client's event source against the repository's EventSource convention: each
/// command starts and stops, or fails, once; an observer hook that throws is reported once; and no
/// key or value is written, not even the key a write-write conflict's server message names.
/// </summary>
[Collection(nameof(KeyValueClientEventSourceCollection))]
public sealed class KeyValueClientEventSourceTests
{
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should be named for its assembly")]
    public void GetName_KeyValueClientEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(KeyValueClientEventSource));

        // Assert
        name.ShouldBe(typeof(KeyValueClientEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.KeyValuePair.Client");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(KeyValueClientEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.KeyValuePair.Client", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should report each command and each throwing observer hook once, with its declared payload")]
    public async Task Commands_SucceedAndFail_ShouldReportEachOnce()
    {
        // Arrange: an observer whose every hook throws.
        await using var harness = await KeyValueClientTestHarness.StartAsync(observer: new ThrowingObserver());
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        using var recorder = new KeyValueClientEventRecorder();

        // Act: a put binds a key and a value; a scan with a negative limit is a parse failure.
        long etag = await connection.PutAsync(Encoding.UTF8.GetBytes("secret-key"), Encoding.UTF8.GetBytes("secret-value"), KeyValueClientTestHarness.Timeout());
        var failure = await Should.ThrowAsync<KeyValueClientException>(async () =>
            await connection.ScanAsync(new KeyValueScanRange { Limit = -1 }, KeyValueClientTestHarness.Timeout()));

        // Assert: the throwing hooks changed no outcome.
        etag.ShouldBeGreaterThan(0L);
        connection.IsOpen.ShouldBeTrue();

        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], KeyValueClientTestHarness.DatabaseName)).ToArray();
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
        (start.Keywords & KeyValueClientEventSource.Keywords.Commands).ShouldBe(KeyValueClientEventSource.Keywords.Commands);
        start.PayloadNames.ShouldBe(["database", "parameterCount"]);
        start.Payload.ShouldBe([KeyValueClientTestHarness.DatabaseName, 2]);

        var stop = events[2];
        stop.EventId.ShouldBe(2);
        stop.PayloadNames.ShouldBe(["database", "status", "rowCount", "affectedCount", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Success");
        stop.Payload[2].ShouldBe(1L);

        var failed = events[6];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "errorKind", "code", "exceptionType", "durationMilliseconds"]);
        failed.Payload![1].ShouldBe(failure.Kind.ToString());
        failed.Payload[2].ShouldBe(failure.Code.ToString());
        failed.Payload[3].ShouldBe(typeof(KeyValueClientException).FullName);
        events[7].Payload!.Take(4).ShouldBe([KeyValueClientTestHarness.DatabaseName, "Error", -1L, -1L]);

        var observerFailed = events[8];
        observerFailed.EventId.ShouldBe(4);
        observerFailed.Level.ShouldBe(EventLevel.Warning);
        observerFailed.PayloadNames.ShouldBe(["database", "callback", "exceptionType", "exceptionMessage"]);
        observerFailed.Payload.ShouldBe([KeyValueClientTestHarness.DatabaseName, "OnFailed", typeof(InvalidOperationException).FullName, "OnFailed failed"]);

        // No payload carries a key, a value or the command text.
        events.SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value =>
            value.Contains("secret", StringComparison.Ordinal) || value.Contains("PUT", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should close a cancelled command with a Cancelled stop and no failure")]
    public async Task Command_Cancelled_ShouldWriteACancelledStop()
    {
        // Arrange
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        using var recorder = new KeyValueClientEventRecorder();

        // Act: a token canceled before the command's exchange writes its first frame.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await connection.ExistsAsync(Encoding.UTF8.GetBytes("key"), new System.Threading.CancellationToken(canceled: true)));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], KeyValueClientTestHarness.DatabaseName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["CommandStart", "CommandStop"]);
        events[1].Payload!.Take(4).ShouldBe([KeyValueClientTestHarness.DatabaseName, "Cancelled", -1L, -1L]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should report a write-write conflict without the key the server's message names")]
    public async Task PutAsync_WriteWriteConflict_ShouldReportFailureWithoutKey()
    {
        // Arrange: a transaction on the server session takes its snapshot, then another session
        // inserts a key the snapshot cannot see, so the client's insert of the same key loses
        // first-updater-wins and the server names the key in hexadecimal in its message.
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        byte[] key = Encoding.UTF8.GetBytes("secret-key");
        byte[] unrelated = Encoding.UTF8.GetBytes("unrelated");
        await connection.PutAsync(unrelated, unrelated, KeyValueClientTestHarness.Timeout());
        var serverSession = harness.Server.Sessions.ShouldHaveSingleItem();
        var transaction = await serverSession.DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(KeyValueClientTestHarness.Timeout());
        (await connection.ExistsAsync(unrelated, KeyValueClientTestHarness.Timeout())).ShouldBeTrue();
        harness.Engine.TryGetDatabase(KeyValueClientTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using (var other = await database.CreateSessionAsync())
        {
            await database.PutAsync(other, key, Encoding.UTF8.GetBytes("concurrent"), cancellationToken: KeyValueClientTestHarness.Timeout());
        }

        using var recorder = new KeyValueClientEventRecorder();

        // Act
        var failure = await Should.ThrowAsync<KeyValueClientException>(async () =>
            await connection.PutAsync(key, Encoding.UTF8.GetBytes("secret-value"), KeyValueClientTestHarness.Timeout()));
        await transaction.RollbackAsync(KeyValueClientTestHarness.Timeout());

        // Assert: the server's message names the key; no event of the client or its core does.
        string keyHex = Convert.ToHexString(key);
        failure.Message.ShouldContain(keyHex, Case.Sensitive);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        recorder.CoreEvents.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var failed = recorder.Events
            .Where(e => Equals(e.Payload?[0], KeyValueClientTestHarness.DatabaseName) && e.EventName == "CommandFailed")
            .ShouldHaveSingleItem();
        failed.PayloadNames.ShouldBe(["database", "errorKind", "code", "exceptionType", "durationMilliseconds"]);
        failed.Payload![3].ShouldBe(typeof(KeyValueClientException).FullName);

        var exchangeFailed = recorder.CoreEvents
            .Where(e => Equals(e.Payload?[0], KeyValueClientTestHarness.DatabaseName) && e.EventName == "ExchangeFailed")
            .ShouldHaveSingleItem();
        exchangeFailed.PayloadNames.ShouldBe(["database", "code", "exceptionType"]);
        exchangeFailed.Payload![2].ShouldBe("Assimalign.Cohesion.Database.Client.DatabaseClientException", "The core writes the failure's type, never the server's message.");

        recorder.Events.Concat(recorder.CoreEvents).SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value =>
            value.Contains(keyHex, StringComparison.OrdinalIgnoreCase) || value.Contains("secret", StringComparison.Ordinal));
    }

    /// <summary>
    /// An observer whose every hook throws.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair.Client] - KeyValueClientEventSource: Should write an uncoded failure with empty kind and code, and a stop only for a command whose start was written")]
    public async Task CommandEnded_UncodedFailure_ShouldWriteEmptyCodeAndStopOnlyWhenStarted()
    {
        // Arrange: a failure the client raises itself (an overlapping exchange) carries no wire code.
        await using var harness = await KeyValueClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(KeyValueClientTestHarness.Timeout());
        using var recorder = new KeyValueClientEventRecorder();
        var failure = new InvalidOperationException("An exchange is already active on this connection.");

        // Act: one command whose start a listener that attached late never saw, and one it saw.
        KeyValueClientEventSource.Log.CommandEnded(connection, startWritten: false, failure, -1, -1, Stopwatch.GetTimestamp());
        KeyValueClientEventSource.Log.CommandEnded(connection, startWritten: true, failure, -1, -1, Stopwatch.GetTimestamp());

        // Assert: the failure is written either way, the stop only after a written start.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => Equals(e.Payload?[0], KeyValueClientTestHarness.DatabaseName)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["CommandFailed", "CommandFailed", "CommandStop"]);
        events[0].Payload!.Take(4).ShouldBe([KeyValueClientTestHarness.DatabaseName, string.Empty, string.Empty, typeof(InvalidOperationException).FullName]);
        events[2].Payload!.Take(4).ShouldBe([KeyValueClientTestHarness.DatabaseName, "Error", -1L, -1L]);
    }

    private sealed class ThrowingObserver : KeyValueClientObserver
    {
        protected internal override void OnExecuting(string commandText, int parameterCount)
            => throw new InvalidOperationException("OnExecuting failed");

        protected internal override void OnExecuted(string commandText, long rowCount, long affectedCount, TimeSpan elapsed)
            => throw new InvalidOperationException("OnExecuted failed");

        protected internal override void OnFailed(string commandText, KeyValueClientException exception, TimeSpan elapsed)
            => throw new InvalidOperationException("OnFailed failed");
    }

    /// <summary>
    /// Records the events the key-value client's event source writes, and separately those of the
    /// shared client core's source, which this test assembly enables by name.
    /// </summary>
    private sealed class KeyValueClientEventRecorder : EventListener
    {
        private const string ClientCoreSourceName = "Assimalign.Cohesion.Database.Client";

        // Field initializers run before the base constructor, which calls OnEventSourceCreated for
        // the sources that already exist.
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
        private readonly ConcurrentQueue<EventWrittenEventArgs> _coreEvents = new();

        public KeyValueClientEventRecorder()
        {
            EnableEvents(KeyValueClientEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        public IReadOnlyList<EventWrittenEventArgs> CoreEvents => _coreEvents.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == ClientCoreSourceName)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, KeyValueClientEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
            else if (eventData.EventSource.Name == ClientCoreSourceName)
            {
                _coreEvents.Enqueue(eventData);
            }
        }
    }
}
