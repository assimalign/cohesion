using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// key or value is written.
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
        stop.PayloadNames.ShouldBe(["database", "rowCount", "affectedCount", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe(1L);

        var failed = events[6];
        failed.EventId.ShouldBe(3);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "errorKind", "code", "exceptionMessage", "durationMilliseconds"]);
        failed.Payload![1].ShouldBe(failure.Kind.ToString());
        failed.Payload[2].ShouldBe(failure.Code.ToString());
        failed.Payload[3].ShouldBe(failure.Message);

        var observerFailed = events[7];
        observerFailed.EventId.ShouldBe(4);
        observerFailed.Level.ShouldBe(EventLevel.Warning);
        observerFailed.PayloadNames.ShouldBe(["database", "callback", "exceptionType", "exceptionMessage"]);
        observerFailed.Payload.ShouldBe([KeyValueClientTestHarness.DatabaseName, "OnFailed", typeof(InvalidOperationException).FullName, "OnFailed failed"]);

        // No payload carries a key, a value or the command text.
        events.SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(value =>
            value.Contains("secret", StringComparison.Ordinal) || value.Contains("PUT", StringComparison.Ordinal));
    }

    /// <summary>
    /// An observer whose every hook throws.
    /// </summary>
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
    /// Records the events the key-value client's event source writes.
    /// </summary>
    private sealed class KeyValueClientEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public KeyValueClientEventRecorder()
        {
            EnableEvents(KeyValueClientEventSource.Log, EventLevel.Verbose, EventKeywords.All);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (ReferenceEquals(eventData.EventSource, KeyValueClientEventSource.Log))
            {
                _events.Enqueue(eventData);
            }
        }
    }
}
