using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

/// <summary>
/// Serializes the tests that observe the key-value model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(KeyValueDatabaseEventSourceCollection), DisableParallelization = true)]
public class KeyValueDatabaseEventSourceCollection
{
}

/// <summary>
/// The key-value model's event source against the repository's EventSource convention, and the server
/// events it writes for real sessions over the in-memory driver: each event once, with its
/// declared payload, and the server-session gauge back where it started (database event-sources
/// plan, section 4.9). The SQL, Graph and Blob suites check the same ids, names and payloads
/// against their own sources.
/// </summary>
[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueDatabaseEventSourceTests
{
    private static readonly string[] SessionClosedPayload = ["sessionId", "reason", "durationMilliseconds"];

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should be named for its assembly")]
    public void GetName_KeyValueDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(KeyValueDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(KeyValueDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.KeyValuePair");
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(KeyValueDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.KeyValuePair", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report an accepted session and its close once each and restore the gauge")]
    public async Task SessionLifecycle_AcceptAndTerminate_ShouldReportEachOnceAndRestoreTheGauge()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        long currentBefore = KeyValueDatabaseEventSource.Log.CurrentServerSessions;
        long totalBefore = KeyValueDatabaseEventSource.Log.TotalServerSessions;
        long currentWhileOpen;

        // Act
        await using (var harness = await KeyValueServerHarness.StartAsync(configureEngine: options => options.EngineName = engineName))
        {
            await using var client = await harness.DialAsync();
            await client.HandshakeAsync();
            currentWhileOpen = KeyValueDatabaseEventSource.Log.CurrentServerSessions;
            await client.SendAsync(ProtocolMessageType.Terminate);
            (await client.ReadAsync()).ShouldBeNull();
            await KeyValueServerHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 1);
        KeyValueDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        KeyValueDatabaseEventSource.Log.TotalServerSessions.ShouldBe(totalBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var accepted = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        accepted.EventName.ShouldBe("SessionAccepted");
        accepted.Level.ShouldBe(EventLevel.Verbose);
        (accepted.Keywords & KeyValueDatabaseEventSource.Keywords.Sessions).ShouldBe(KeyValueDatabaseEventSource.Keywords.Sessions);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report a connection beyond the session limit as rejected once")]
    public async Task SessionRejected_BeyondMaxSessions_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = KeyValueDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await KeyValueServerHarness.StartAsync(options => options.MaxSessions = 1, options => options.EngineName = engineName);
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
        KeyValueDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report each handshake refusal once with its code")]
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
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(
            options => options.Authenticator = refusal == "AuthenticationFailed" ? new RejectingAuthenticator() : null,
            options => options.EngineName = engineName);
        if (refusal == "DatabaseOffline")
        {
            var open = await harness.Engine.OpenDatabaseAsync(KeyValueServerHarness.DatabaseName);
            open.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        }

        await using var client = await harness.DialAsync();
        string database = refusal == "DatabaseNotFound" ? "no-such-db" : KeyValueServerHarness.DatabaseName;
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report a handshake that outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_SilentPeer_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200), options => options.EngineName = engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report a handshake whose authenticator outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_StallingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange: the timeout lapses inside the authenticator, not at a read.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = new StallingAuthenticator();
                options.AuthenticationTimeout = TimeSpan.FromSeconds(2);
            },
            options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, KeyValueServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should cut the names an unauthenticated peer sent to their bound in a handshake refusal")]
    public async Task HandshakeRefused_OversizedStartup_ShouldWriteBoundedNames()
    {
        // Arrange: a startup refused before any lookup, naming a database and principal far past
        // the 256-character payload bound.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(configureEngine: options => options.EngineName = engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report a session the idle timeout evicts as closed once")]
    public async Task SessionClosed_IdleTimeout_ShouldBeReportedOnceWithItsReason()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200), options => options.EngineName = engineName);
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

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report a protocol violation once and the session closed for it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionProtocolViolation_ReadyState_ShouldBeReportedOnce(bool malformedParameter)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(configureEngine: options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a frame the ready state does not take, or an execute whose parameter fails to decode.
        if (malformedParameter)
        {
            var message = new ProtocolExecuteMessage("GET @broken", new Dictionary<string, byte[]> { ["broken"] = [] });
            await client.SendAsync(ProtocolMessageType.Execute, message.Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report an unexpected fault in the exchange as an error once")]
    public async Task SessionFaulted_ThrowingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await KeyValueServerHarness.StartAsync(options => options.Authenticator = new FaultingAuthenticator(), options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, KeyValueServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should report the sessions a lapsed drain aborts once")]
    public async Task SessionsAborted_DrainTimesOut_ShouldBeReportedOnce()
    {
        // Arrange: a session held inside its handshake, which the soft stop cannot end.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        var authenticator = new BlockingAuthenticator();
        await using var harness = await KeyValueServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = authenticator;
                options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100);
            },
            options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, KeyValueServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should publish its server-session counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishServerSessionCounters()
    {
        // Arrange
        string[] counters = ["current-server-sessions", "total-server-sessions", "total-rejected-sessions"];

        // Act
        using var recorder = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, TestTimeout.Token(30));
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - KeyValueDatabaseEventSource: Should allocate nothing on any write while nobody listens")]
    public async Task Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source (a disposed listener leaves it enabled until a disable command).
        await using var engine = KeyValueDatabaseEngine.Create(new KeyValueDatabaseEngineOptions { EngineName = UniqueEngineName() });
        var session = new StubServerSession();
        var failure = new InvalidOperationException("failure");
        using (var listener = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(KeyValueDatabaseEventSource.Log);
        }

        KeyValueDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        WriteEveryEvent(engine, session, failure);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        long timestamp = WriteEveryEvent(engine, session, failure);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        allocated.ShouldBe(0);
        timestamp.ShouldBe(0);
    }

    private static long WriteEveryEvent(DatabaseEngine engine, DatabaseServerSession session, Exception failure)
    {
        var log = KeyValueDatabaseEventSource.Log;
        long timestamp = log.SessionTimestamp();
        log.SessionAccepted(engine, session, 1);
        log.SessionRejected(engine, KeyValueDatabaseEventSource.RejectReason.SessionLimit, 1, 1);
        log.HandshakeRefused(session, "kv", "ada", ProtocolErrorCode.AuthenticationFailed, "refused");
        log.HandshakeTimedOut(session, TimeSpan.FromSeconds(1));
        log.SessionProtocolViolation(session, "violation");
        log.SessionFaulted(session, failure);
        log.SessionCleanupFailed(session, failure);
        log.SessionClosed(session, KeyValueDatabaseEventSource.CloseReason.Terminated, timestamp);
        log.SessionsAborted(engine, 1, TimeSpan.FromSeconds(1));
        return timestamp;
    }

    private static string UniqueEngineName() => "kv-events-" + Guid.NewGuid().ToString("N");

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

    /// <summary>A server session with no pump, for the writes the allocation check makes.</summary>
    private sealed class StubServerSession : DatabaseServerSession
    {
        public override DatabaseSession? DatabaseSession => null;

        protected override ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
    }
}
