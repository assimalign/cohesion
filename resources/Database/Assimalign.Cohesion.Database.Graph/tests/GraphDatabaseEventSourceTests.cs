using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

/// <summary>
/// Serializes the tests that observe the graph model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(GraphDatabaseEventSourceCollection), DisableParallelization = true)]
public class GraphDatabaseEventSourceCollection
{
}

/// <summary>
/// The graph model's event source against the repository's EventSource convention, and the server
/// events it writes for real sessions over the in-memory driver: each event once, with its
/// declared payload, and the server-session gauge back where it started (database event-sources
/// plan, section 4.9). The SQL, Key-value and Blob suites check the same ids, names and payloads
/// against their own sources. The model's own events (section 4.10) follow: the index recovery of
/// a reopened database, and the wire statement that fails to parse before the root sees it.
/// </summary>
[Collection(nameof(GraphDatabaseEventSourceCollection))]
public sealed class GraphDatabaseEventSourceTests
{
    private static readonly string[] SessionClosedPayload = ["sessionId", "reason", "durationMilliseconds"];

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should be named for its assembly")]
    public void GetName_GraphDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(GraphDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(GraphDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Graph");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(GraphDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Graph", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report an accepted session and its close once each and restore the gauge")]
    public async Task SessionLifecycle_AcceptAndTerminate_ShouldReportEachOnceAndRestoreTheGauge()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        long currentBefore = GraphDatabaseEventSource.Log.CurrentServerSessions;
        long totalBefore = GraphDatabaseEventSource.Log.TotalServerSessions;
        long currentWhileOpen;

        // Act
        await using (var harness = await GraphServerHarness.StartAsync(engineName: engineName))
        {
            await using var client = await harness.DialAsync();
            await client.HandshakeAsync();
            currentWhileOpen = GraphDatabaseEventSource.Log.CurrentServerSessions;
            await client.SendAsync(ProtocolMessageType.Terminate);
            (await client.ReadAsync()).ShouldBeNull();
            await GraphServerHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 1);
        GraphDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        GraphDatabaseEventSource.Log.TotalServerSessions.ShouldBe(totalBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var accepted = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        accepted.EventName.ShouldBe("SessionAccepted");
        accepted.Level.ShouldBe(EventLevel.Verbose);
        (accepted.Keywords & GraphDatabaseEventSource.Keywords.Sessions).ShouldBe(GraphDatabaseEventSource.Keywords.Sessions);
        accepted.PayloadNames.ShouldBe(["engineName", "sessionId", "activeSessions"]);
        accepted.Payload![2].ShouldBe(1);

        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem();
        closed.EventName.ShouldBe("SessionClosed");
        closed.Level.ShouldBe(EventLevel.Verbose);
        closed.PayloadNames.ShouldBe(SessionClosedPayload);
        closed.Payload![1].ShouldBe("Terminated");
        ((double)closed.Payload[2]!).ShouldBeGreaterThan(0);
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([5]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a connection beyond the session limit as rejected once")]
    public async Task SessionRejected_BeyondMaxSessions_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = GraphDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await GraphServerHarness.StartAsync(options => options.MaxSessions = 1, engineName: engineName);
        await using var first = await harness.DialAsync();
        await first.HandshakeAsync();

        // Act
        await using var second = await harness.DialAsync();
        await second.ExpectAsync(ProtocolMessageType.Error);
        var rejected = await recorder.WaitForAsync(e => e.EventId == 2 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));

        // Assert
        recorder.Events.Where(e => e.EventId == 2 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        rejected.EventName.ShouldBe("SessionRejected");
        rejected.Level.ShouldBe(EventLevel.Warning);
        rejected.PayloadNames.ShouldBe(["engineName", "reason", "activeSessions", "maxSessions"]);
        rejected.Payload.ShouldBe([engineName, "SessionLimit", 1, 1]);
        GraphDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report each handshake refusal once with its code")]
    [InlineData("StartupExpected", nameof(ProtocolErrorCode.ProtocolViolation))]
    [InlineData("UnsupportedVersion", nameof(ProtocolErrorCode.UnsupportedVersion))]
    [InlineData("DatabaseNotFound", nameof(ProtocolErrorCode.DatabaseNotFound))]
    [InlineData("AuthenticateResponseExpected", nameof(ProtocolErrorCode.ProtocolViolation))]
    [InlineData("AuthenticationFailed", nameof(ProtocolErrorCode.AuthenticationFailed))]
    [InlineData("DatabaseOffline", nameof(ProtocolErrorCode.Unavailable))]
    public async Task HandshakeRefused_EachRefusal_ShouldBeReportedOnceWithItsCode(string refusal, string code)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(
            options => options.Authenticator = refusal == "AuthenticationFailed" ? new RejectingAuthenticator() : null,
            engineName: engineName);
        if (refusal == "DatabaseOffline")
        {
            var open = await harness.Engine.OpenDatabaseAsync(GraphServerHarness.DatabaseName);
            open.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        }

        await using var client = await harness.DialAsync();
        string database = refusal == "DatabaseNotFound" ? "no-such-db" : GraphServerHarness.DatabaseName;
        var version = refusal == "UnsupportedVersion" ? new ProtocolVersion(99, 0) : ProtocolVersion.Current;

        // Act
        if (refusal == "StartupExpected")
        {
            await client.SendAsync(ProtocolMessageType.Ping);
        }
        else
        {
            await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(version, database, "mallory").Encode());
            if (refusal is "AuthenticateResponseExpected" or "AuthenticationFailed" or "DatabaseOffline")
            {
                await client.ExpectAsync(ProtocolMessageType.Authenticate);
                await client.SendAsync(refusal == "AuthenticateResponseExpected" ? ProtocolMessageType.Ping : ProtocolMessageType.AuthenticateResponse);
            }
        }

        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        error.Code.ToString().ShouldBe(code);
        var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
        refused.EventName.ShouldBe("HandshakeRefused");
        refused.Level.ShouldBe(EventLevel.Warning);
        refused.PayloadNames.ShouldBe(["sessionId", "database", "principal", "code", "detail"]);
        refused.Payload![1].ShouldBe(refusal == "StartupExpected" ? string.Empty : database);
        refused.Payload[2].ShouldBe(refusal == "StartupExpected" ? string.Empty : "mallory");
        refused.Payload[3].ShouldBe(code);
        refused.Payload[4].ShouldBe(error.Message);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeRefused");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([3, 5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a handshake that outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_SilentPeer_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act: send nothing.
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        var timedOut = recorder.Events.Where(e => IsFor(e, 4, sessionId)).ShouldHaveSingleItem();
        timedOut.EventName.ShouldBe("HandshakeTimedOut");
        timedOut.Level.ShouldBe(EventLevel.Warning);
        timedOut.PayloadNames.ShouldBe(["sessionId", "timeoutMilliseconds"]);
        timedOut.Payload![1].ShouldBe(200d);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeTimedOut");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a handshake whose authenticator outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_StallingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange: the timeout lapses inside the authenticator, not at a read.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = new StallingAuthenticator();
                options.AuthenticationTimeout = TimeSpan.FromSeconds(2);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, GraphServerHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: dropped without an error frame and reported as the timeout, not as a cancellation.
        recorder.Events.Where(e => IsFor(e, 4, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe(2000d);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeTimedOut");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([4, 5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should cut the names an unauthenticated peer sent to their bound in a handshake refusal")]
    public async Task HandshakeRefused_OversizedStartup_ShouldWriteBoundedNames()
    {
        // Arrange: a startup refused before any lookup, naming a database and principal far past
        // the 256-character payload bound.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(new ProtocolVersion(99, 0), new string('d', 100_000), new string('p', 100_000)).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
        refused.Payload.ShouldBe([sessionId, new string('d', 256) + "...", new string('p', 256) + "...", nameof(ProtocolErrorCode.UnsupportedVersion), error.Message]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a session the idle timeout evicts as closed once")]
    public async Task SessionClosed_IdleTimeout_ShouldBeReportedOnceWithItsReason()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: go idle.
        await client.ExpectAsync(ProtocolMessageType.Error);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        closed.Payload![1].ShouldBe("IdleTimeout");
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([5]);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a protocol violation once and the session closed for it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionProtocolViolation_ReadyState_ShouldBeReportedOnce(bool malformedParameter)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a frame the ready state does not take, or an execute whose parameter fails to decode.
        if (malformedParameter)
        {
            var message = new GraphProtocolExecuteMessage("MATCH (n) RETURN n.name", new Dictionary<string, byte[]> { ["broken"] = [] });
            await client.SendAsync(GraphProtocolMessageType.Execute, message.Encode());
        }
        else
        {
            await client.SendAsync(ProtocolMessageType.Authenticate);
        }

        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        var violation = recorder.Events.Where(e => IsFor(e, 6, sessionId)).ShouldHaveSingleItem();
        violation.EventName.ShouldBe("SessionProtocolViolation");
        violation.Level.ShouldBe(EventLevel.Warning);
        violation.PayloadNames.ShouldBe(["sessionId", "exceptionMessage"]);
        violation.Payload![1].ShouldBe(error.Message);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("ProtocolViolation");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report an unexpected fault in the exchange as an error once")]
    public async Task SessionFaulted_ThrowingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await GraphServerHarness.StartAsync(options => options.Authenticator = new FaultingAuthenticator(), engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, GraphServerHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: the peer gets an internal error only; the event carries the failure.
        error.Code.ShouldBe(ProtocolErrorCode.Internal);
        error.Message.ShouldNotContain(FaultingAuthenticator.FaultMessage);
        var faulted = recorder.Events.Where(e => IsFor(e, 7, sessionId)).ShouldHaveSingleItem();
        faulted.EventName.ShouldBe("SessionFaulted");
        faulted.Level.ShouldBe(EventLevel.Error);
        faulted.PayloadNames.ShouldBe(["sessionId", "exceptionType", "exceptionMessage"]);
        faulted.Payload.ShouldBe([sessionId, typeof(InvalidOperationException).FullName, FaultingAuthenticator.FaultMessage]);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("Faulted");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report the sessions a lapsed drain aborts once")]
    public async Task SessionsAborted_DrainTimesOut_ShouldBeReportedOnce()
    {
        // Arrange: a session held inside its handshake, which the soft stop cannot end.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        var authenticator = new BlockingAuthenticator();
        await using var harness = await GraphServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = authenticator;
                options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, GraphServerHarness.DatabaseName, "ada").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);
        await authenticator.Entered.WaitAsync(TestTimeout.Token(30));

        // Act
        Task stop = harness.Server.StopAsync();
        var aborted = await recorder.WaitForAsync(e => e.EventId == 9 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));
        authenticator.Release(authenticated: true);
        await stop.WaitAsync(TestTimeout.Token(30));
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        recorder.Events.Where(e => e.EventId == 9 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        aborted.EventName.ShouldBe("SessionsAborted");
        aborted.Level.ShouldBe(EventLevel.Warning);
        aborted.PayloadNames.ShouldBe(["engineName", "sessions", "drainTimeoutMilliseconds"]);
        aborted.Payload.ShouldBe([engineName, 1, 100d]);
        harness.Server.Sessions.ShouldBeEmpty();
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should publish its server-session counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishServerSessionCounters()
    {
        // Arrange
        string[] counters = ["current-server-sessions", "total-server-sessions", "total-rejected-sessions"];

        // Act
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, TestTimeout.Token(30));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should allocate nothing on any write while nobody listens")]
    public async Task Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source. Disposing the last listener already disables it; the explicit
        // disable below is redundant but harmless.
        await using var engine = GraphDatabaseEngine.Create(UniqueEngineName(), new GraphDatabaseEngineOptions());
        var session = new StubServerSession();
        var failure = new InvalidOperationException("failure");
        using (var listener = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(GraphDatabaseEventSource.Log);
        }

        GraphDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        WriteEveryEvent(engine, session, failure);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        long timestamp = WriteEveryEvent(engine, session, failure);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
        timestamp.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report the index recovery of a database reopened with an aborted writer once")]
    public async Task IndexRecovery_ReopenWithAbortedWriter_ShouldReportStartAndStopOnce()
    {
        // Arrange: quiet workers, and a transaction whose write reached the journal (a page
        // write-back pass flushes the journal ahead of the pages, as the recovery fixture does) when
        // the database went offline under it, so its close writes nothing and the reopen recovers it.
        var token = TestTimeout.Token(60);
        string name = "recovery" + Guid.NewGuid().ToString("N");
        await using var engine = GraphDatabaseEngine.Create(UniqueEngineName(), new GraphDatabaseEngineOptions
        {
            CheckpointInterval = TimeSpan.FromHours(1),
            PageWriteBackInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync(name, token);
        var session = await database.CreateSessionAsync(token);
        await session.BeginTransactionAsync(token);
        await session.ExecuteAsync("INSERT (:Pending {name: 'aborted'})", cancellationToken: token);
        foreach (var worker in engine.Workers.OfType<DatabaseEngineWorker>().Where(worker => worker.Kind == DatabaseEngineWorkerKind.PageWriteBack))
        {
            worker.RunIteration(CancellationToken.None).ShouldBeTrue(worker.Fault?.ToString());
        }

        database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        await session.DisposeAsync();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        var reopened = await engine.OpenDatabaseAsync(name, token);

        // Assert
        reopened.ShouldNotBeSameAs(database);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var start = recorder.Events.Where(e => e.EventId == 10 && Equals(e.Payload![0], name)).ShouldHaveSingleItem();
        start.EventName.ShouldBe("IndexRecoveryStart");
        start.Level.ShouldBe(EventLevel.Informational);
        start.PayloadNames.ShouldBe(["database", "abortedWriters"]);
        ((int)start.Payload![1]!).ShouldBeGreaterThanOrEqualTo(1);
        var stop = recorder.Events.Where(e => e.EventId == 11 && Equals(e.Payload![0], name)).ShouldHaveSingleItem();
        stop.EventName.ShouldBe("IndexRecoveryStop");
        stop.Level.ShouldBe(EventLevel.Informational);
        stop.PayloadNames.ShouldBe(["database", "status", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Success");
        ((double)stop.Payload![2]!).ShouldBeGreaterThan(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should close a recovery that threw with an Error stop, by a direct write")]
    public void IndexRecoveryStop_RecoveryThrew_ShouldWriteAnErrorStop()
    {
        // Arrange: no fault-injection seam reaches RecoverIndexesAsync inside the open, so the
        // status a recovery that threw writes is checked by a direct write.
        string name = "threw" + Guid.NewGuid().ToString("N");
        var database = new DatabaseName(name);
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Informational);

        // Act
        long started = GraphDatabaseEventSource.Log.IndexRecoveryStart(database, 1);
        GraphDatabaseEventSource.Log.IndexRecoveryStop(database, succeeded: false, started);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.Events.Where(e => e.EventId is 10 or 11 && Equals(e.Payload![0], name)).ToArray();
        events.Select(e => e.EventName).ShouldBe(["IndexRecoveryStart", "IndexRecoveryStop"]);
        events[1].Payload![1].ShouldBe("Error");
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should report a wire statement that fails to parse once, before the root sees it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatementParseFailed_WireSyntaxError_ShouldBeReportedOnce(bool paths)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        using var root = new RootEventRecorder();
        await using var harness = await GraphServerHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);

        // Act: a Cypher arrow, the GQL0008 syntax error.
        await client.SendAsync(paths ? GraphProtocolMessageType.ExecutePaths : GraphProtocolMessageType.Execute,
            GraphProtocolExecuteMessage.Create("MATCH (a)-->(b) RETURN a").Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);

        // Assert: one parse failure, carrying what the client was told, and the session ready.
        error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        var failed = recorder.Events.Where(e => e.EventId == 12 && Equals(e.Payload![0], sessionId)).ShouldHaveSingleItem();
        failed.EventName.ShouldBe("StatementParseFailed");
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["sessionId", "database", "code", "exceptionType"]);
        failed.Payload.ShouldBe([sessionId, GraphServerHarness.DatabaseName, string.Empty, typeof(DatabaseParseException).FullName]);
        failed.Payload!.OfType<string>().ShouldNotContain(error.Message, "A parse error quotes the statement; the event writes its type only.");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);

        // The root's StatementFailed (event 28) is not written for it, nor is its StatementStart: the
        // statement failed before the root session saw it.
        // The server session's root session is found by its engine, which is this test's alone.
        long sessionNumber = (long)root.Events.Where(e => e.EventId == 23 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem().Payload![2]!;
        root.Events.Where(e => (e.EventId == 25 || e.EventId == 28) && Equals(e.Payload![1], sessionNumber)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should not report an in-process statement that fails to parse, which the root reports")]
    public async Task StatementParseFailed_InProcessSyntaxError_ShouldNotBeReported()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        using var root = new RootEventRecorder();
        await using var engine = GraphDatabaseEngine.Create(engineName, new GraphDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("graph", TestTimeout.Token(30));
        await using var session = await database.CreateSessionAsync(TestTimeout.Token(30));

        // Act
        var failure = await Should.ThrowAsync<DatabaseParseException>(async () =>
            await session.ExecuteAsync("MATCH (a)-->(b) RETURN a", cancellationToken: TestTimeout.Token(30)));

        // Assert: the in-process text path is the root's: its StatementFailed exactly once, by the
        // failure's type and never its message, and Graph's event 12 not at all.
        failure.Message.ShouldStartWith("GQL parse error GQL0008: ", Case.Sensitive);
        recorder.Events.ShouldNotContain(e => e.EventId == 12);
        long sessionNumber = (long)root.Events.Where(e => e.EventId == 23 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem().Payload![2]!;
        var failed = root.Events.Where(e => e.EventId == 28 && Equals(e.Payload![1], sessionNumber)).ShouldHaveSingleItem();
        failed.PayloadNames.ShouldBe(["database", "sessionNumber", "requestKind", "code", "exceptionType", "durationMilliseconds"]);
        failed.Payload![4].ShouldBe(typeof(DatabaseParseException).FullName);
        failed.Payload!.OfType<string>().ShouldNotContain(failure.Message);
    }

    private static long WriteEveryEvent(DatabaseEngine engine, DatabaseServerSession session, Exception failure)
    {
        var log = GraphDatabaseEventSource.Log;
        log.IndexRecoveryStop(new DatabaseName("graph"), true, log.IndexRecoveryStart(new DatabaseName("graph"), 1));
        log.StatementParseFailed(session, new DatabaseName("graph"), failure);
        long timestamp = log.SessionTimestamp();
        log.SessionAccepted(engine, session, 1);
        log.SessionRejected(engine, GraphDatabaseEventSource.RejectReason.SessionLimit, 1, 1);
        log.HandshakeRefused(session, "graph", "ada", ProtocolErrorCode.AuthenticationFailed, "refused");
        log.HandshakeTimedOut(session, TimeSpan.FromSeconds(1));
        log.SessionProtocolViolation(session, "violation");
        log.SessionFaulted(session, failure);
        log.SessionCleanupFailed(session, failure);
        log.SessionClosed(session, GraphDatabaseEventSource.CloseReason.Terminated, timestamp);
        log.SessionsAborted(engine, 1, TimeSpan.FromSeconds(1));
        return timestamp;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - GraphDatabaseEventSource: Should close a session whose peer reset its TCP connection with a transport reason, not a fault")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionClosed_PeerResetsTcpConnection_ShouldReportATransportReasonAndNoFault(bool whileServerWrites)
    {
        // Arrange: a real TCP listener; the in-memory driver cannot reset a connection.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(GraphDatabaseEventSource.Log, EventLevel.Verbose);
        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var harness = await GraphServerHarness.StartAsync(options => options.Listener = listener, engineName: engineName);

        // Act: the peer completes its handshake, then resets the connection: while the session idles
        // in its ready loop, or while the server's pong writes fill the socket buffers the peer
        // never drains, so the reset fails a send the pump is blocked in.
        using (Socket socket = await TcpResetPeer.ConnectReadyAsync(listener.EndPoint, GraphServerHarness.DatabaseName, TestTimeout.Token()))
        {
            Task? flood = whileServerWrites ? TcpResetPeer.FloodPingsAsync(socket, 4_000_000) : null;
            if (flood is not null)
            {
                await TcpResetPeer.WaitUntilServerStalledAsync(socket, TimeSpan.FromSeconds(15));
            }

            TcpResetPeer.Reset(socket);
            if (flood is not null)
            {
                // Whether the kernel took the whole flood before the reset does not matter.
                await Record.ExceptionAsync(() => flood);
            }
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: a peer that hung up is an expected outcome (plan D9), never a server fault. The
        // write case accepts only the transport reasons: a PeerClosed there means the flood no
        // longer blocked the pump in a send, and the case stopped testing the classification.
        string seen = string.Join("; ", recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventName + "(" + string.Join(", ", e.Payload!.Skip(1)) + ")"));
        recorder.Events.Where(e => IsFor(e, 7, sessionId)).ShouldBeEmpty("The pump reported the reset as a fault: " + seen);
        string[] expected = whileServerWrites
            ? ["TransportFailed", "ConnectionAborted"]
            : ["PeerClosed", "TransportFailed", "ConnectionAborted", "Cancelled"];
        ((string)closed.Payload![1]!).ShouldBeOneOf(expected, seen);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static string UniqueEngineName() => "graph-events-" + Guid.NewGuid().ToString("N");

    private static async Task<Guid> AcceptedSessionAsync(EventSourceRecorder recorder, string engineName)
    {
        var accepted = await recorder.WaitForAsync(e => e.EventId == 1 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));
        return (Guid)accepted.Payload![1]!;
    }

    // The session events whose first payload field is the session's id.
    private static bool IsForSession(EventWrittenEventArgs written, Guid sessionId)
        => written.EventId is >= 3 and <= 8 && written.Payload is { Count: > 0 } payload && Equals(payload[0], sessionId);

    private static bool IsFor(EventWrittenEventArgs written, int eventId, Guid sessionId)
        => written.EventId == eventId && IsForSession(written, sessionId);

    /// <summary>
    /// Records the root source's events, which this assembly reaches only by name: the source is
    /// internal to the root assembly.
    /// </summary>
    private sealed class RootEventRecorder : EventListener
    {
        private const string RootSourceName = "Assimalign.Cohesion.Database";
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == RootSourceName)
            {
                EnableEvents(eventSource, EventLevel.Verbose, EventKeywords.All);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventSource.Name == RootSourceName)
            {
                _events.Enqueue(eventData);
            }
        }
    }

    /// <summary>A server session with no pump, for the writes the allocation check makes.</summary>
    private sealed class StubServerSession : DatabaseServerSession
    {
        public override DatabaseSession? DatabaseSession => null;

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
