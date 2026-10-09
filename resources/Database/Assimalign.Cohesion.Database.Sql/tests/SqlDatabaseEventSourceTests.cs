using System;
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

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// Serializes the tests that observe the SQL model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(SqlDatabaseEventSourceCollection), DisableParallelization = true)]
public class SqlDatabaseEventSourceCollection
{
}

/// <summary>
/// The SQL model's event source against the repository's EventSource convention, and the server
/// events it writes for real sessions over the in-memory driver: each event once, with its
/// declared payload, and the server-session gauge back where it started (database event-sources
/// plan, section 4.9). The Key-value, Graph and Blob suites check the same ids, names and payloads
/// against their own sources.
/// </summary>
[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlDatabaseEventSourceTests
{
    private static readonly string[] SessionClosedPayload = ["sessionId", "reason", "durationMilliseconds"];

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should be named for its assembly")]
    public void GetName_SqlDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(SqlDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(SqlDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Sql");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(SqlDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Sql", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report an accepted session and its close once each and restore the gauge")]
    public async Task SessionLifecycle_AcceptAndTerminate_ShouldReportEachOnceAndRestoreTheGauge()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        long currentBefore = SqlDatabaseEventSource.Log.CurrentServerSessions;
        long totalBefore = SqlDatabaseEventSource.Log.TotalServerSessions;
        long currentWhileOpen;

        // Act
        await using (var harness = await ServerTestHarness.StartAsync(configureEngine: options => options.EngineName = engineName))
        {
            await using var client = await harness.DialAsync();
            await client.HandshakeAsync();
            currentWhileOpen = SqlDatabaseEventSource.Log.CurrentServerSessions;
            await client.SendAsync(ProtocolMessageType.Terminate);
            (await client.ReadAsync()).ShouldBeNull();
            await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 1);
        SqlDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        SqlDatabaseEventSource.Log.TotalServerSessions.ShouldBe(totalBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var accepted = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        accepted.EventName.ShouldBe("SessionAccepted");
        accepted.Level.ShouldBe(EventLevel.Verbose);
        (accepted.Keywords & SqlDatabaseEventSource.Keywords.Sessions).ShouldBe(SqlDatabaseEventSource.Keywords.Sessions);
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a connection beyond the session limit as rejected once")]
    public async Task SessionRejected_BeyondMaxSessions_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = SqlDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await ServerTestHarness.StartAsync(options => options.MaxSessions = 1, options => options.EngineName = engineName);
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
        SqlDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report each handshake refusal once with its code")]
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
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(
            options => options.Authenticator = refusal == "AuthenticationFailed" ? new RejectingAuthenticator() : null,
            options => options.EngineName = engineName);
        if (refusal == "DatabaseOffline")
        {
            var open = await harness.Engine.OpenDatabaseAsync(ServerTestHarness.DatabaseName);
            open.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        }

        await using var client = await harness.DialAsync();
        string database = refusal == "DatabaseNotFound" ? "no-such-db" : ServerTestHarness.DatabaseName;
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a handshake that outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_SilentPeer_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200), options => options.EngineName = engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a handshake whose authenticator outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_StallingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange: the timeout lapses inside the authenticator, not at a read.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(
            options =>
            {
                options.Authenticator = new StallingAuthenticator();
                options.AuthenticationTimeout = TimeSpan.FromSeconds(2);
            },
            options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should cut the names an unauthenticated peer sent to their bound in a handshake refusal")]
    public async Task HandshakeRefused_OversizedStartup_ShouldWriteBoundedNames()
    {
        // Arrange: a startup refused before any lookup, naming a database and principal far past
        // the 256-character payload bound.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options => options.EngineName = engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a database on a refused data-storage format as a handshake refusal once")]
    public async Task HandshakeRefused_RefusedStorageFormat_ShouldBeReportedOnceAsUnavailable()
    {
        // Arrange: a file-backed database whose catalog marker is forged to format 3, which the
        // engine refuses at open (#1099).
        string engineName = UniqueEngineName();
        string rootPath = Path.Combine(Path.GetTempPath(), "cohesion-sql-events", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var creating = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = engineName, RootPath = rootPath }))
            {
                var created = await creating.CreateDatabaseAsync("format-db");
                await created.Catalog.SetRecordSpaceFormatVersionAsync(3);
            }

            using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
            await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = engineName, RootPath = rootPath });
            await using var listener = new InMemoryConnectionListener();
            await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
            await server.StartAsync();
            Connection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, TestTimeout.Token());
            await using var client = new ProtocolTestClient(connection);

            // Act
            await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "format-db", "ada").Encode());
            var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
            (await client.ReadAsync()).ShouldBeNull();
            Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
            await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

            // Assert
            error.Code.ShouldBe(ProtocolErrorCode.Unavailable);
            error.Message.ShouldContain("uses data-storage format 3");
            var refused = recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem();
            refused.Payload.ShouldBe([sessionId, "format-db", "ada", nameof(ProtocolErrorCode.Unavailable), error.Message]);
            recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeRefused");
            recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([3, 5]);
            recorder.Events.ShouldNotContain(e => e.EventId == 0);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a session the idle timeout evicts as closed once")]
    public async Task SessionClosed_IdleTimeout_ShouldBeReportedOnceWithItsReason()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200), options => options.EngineName = engineName);
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

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report a protocol violation once and the session closed for it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionProtocolViolation_ReadyState_ShouldBeReportedOnce(bool malformedParameter)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(configureEngine: options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a frame the ready state does not take, or an execute whose parameter fails to decode.
        if (malformedParameter)
        {
            var message = new ProtocolExecuteMessage("SELECT id FROM users", new Dictionary<string, byte[]> { ["broken"] = [] });
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report an unexpected fault in the exchange as an error once")]
    public async Task SessionFaulted_ThrowingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await ServerTestHarness.StartAsync(options => options.Authenticator = new FaultingAuthenticator(), options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should report the sessions a lapsed drain aborts once")]
    public async Task SessionsAborted_DrainTimesOut_ShouldBeReportedOnce()
    {
        // Arrange: a session held inside its handshake, which the soft stop cannot end.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        var authenticator = new BlockingAuthenticator();
        await using var harness = await ServerTestHarness.StartAsync(
            options =>
            {
                options.Authenticator = authenticator;
                options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100);
            },
            options => options.EngineName = engineName);
        await using var client = await harness.DialAsync();
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should publish its server-session counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishServerSessionCounters()
    {
        // Arrange
        string[] counters = ["current-server-sessions", "total-server-sessions", "total-rejected-sessions"];

        // Act
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, TestTimeout.Token(30));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should allocate nothing on any write while nobody listens")]
    public async Task Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source. Disposing the last listener already disables it; the explicit
        // disable below is redundant but harmless.
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = UniqueEngineName() });
        var session = new StubServerSession();
        var failure = new InvalidOperationException("failure");
        using (var listener = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(SqlDatabaseEventSource.Log);
        }

        SqlDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
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
        var log = SqlDatabaseEventSource.Log;
        long timestamp = log.SessionTimestamp();
        log.SessionAccepted(engine, session, 1);
        log.SessionRejected(engine, SqlDatabaseEventSource.RejectReason.SessionLimit, 1, 1);
        log.HandshakeRefused(session, "app", "ada", ProtocolErrorCode.AuthenticationFailed, "refused");
        log.HandshakeTimedOut(session, TimeSpan.FromSeconds(1));
        log.SessionProtocolViolation(session, "violation");
        log.SessionFaulted(session, failure);
        log.SessionCleanupFailed(session, failure);
        log.SessionClosed(session, SqlDatabaseEventSource.CloseReason.Terminated, timestamp);
        log.SessionsAborted(engine, 1, TimeSpan.FromSeconds(1));
        return timestamp;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should close a session whose peer reset its TCP connection with a transport reason, not a fault")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionClosed_PeerResetsTcpConnection_ShouldReportATransportReasonAndNoFault(bool whileServerWrites)
    {
        // Arrange: a real TCP listener; the in-memory driver cannot reset a connection.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var engine = SqlDatabaseEngine.Create(new SqlDatabaseEngineOptions { EngineName = engineName });
        await engine.CreateDatabaseAsync(ServerTestHarness.DatabaseName);
        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = SqlDatabaseServer.Create(engine, new SqlDatabaseServerOptions { Listener = listener });
        await server.StartAsync(TestTimeout.Token());

        // Act: the peer completes its handshake, then resets the connection: while the session idles
        // in its ready loop, or while the server's pong writes fill the socket buffers the peer
        // never drains, so the reset fails a send the pump is blocked in.
        using (Socket socket = await TcpResetPeer.ConnectReadyAsync(listener.EndPoint, ServerTestHarness.DatabaseName, TestTimeout.Token()))
        {
            Task? flood = whileServerWrites ? TcpResetPeer.FloodPingsAsync(socket, 4_000_000) : null;
            if (flood is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
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
        // TCP driver reports a reset its receive sees as the stream's end, so an idle session
        // closes as PeerClosed; a reset that fails a send closes it as TransportFailed.
        string seen = string.Join("; ", recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventName + "(" + string.Join(", ", e.Payload!.Skip(1)) + ")"));
        recorder.Events.Where(e => IsFor(e, 7, sessionId)).ShouldBeEmpty("The pump reported the reset as a fault: " + seen);
        ((string)closed.Payload![1]!).ShouldBeOneOf(["PeerClosed", "TransportFailed", "ConnectionAborted", "Cancelled"], seen);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - SqlDatabaseEventSource: Should close a session once and restore the gauge when its connection's disposal throws")]
    public async Task SessionClosed_ConnectionDisposalThrows_ShouldStillCloseOnceAndRestoreTheGauge()
    {
        // Arrange: every accepted connection throws from its disposal.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(SqlDatabaseEventSource.Log, EventLevel.Verbose);
        await using var inner = new InMemoryConnectionListener();
        var listener = new DisposeFailingConnectionListener(inner);
        await using var harness = await ServerTestHarness.StartAsync(options => options.Listener = listener, options => options.EngineName = engineName);
        long currentBefore = SqlDatabaseEventSource.Log.CurrentServerSessions;

        // Act: a session that terminates cleanly, whose cleanup then meets the failing disposal.
        await using (var client = new ProtocolTestClient(await inner.CreateFactory().ConnectAsync(inner.EndPoint, TestTimeout.Token())))
        {
            await client.HandshakeAsync();
            await client.SendAsync(ProtocolMessageType.Terminate);
            Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
            await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));
            await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);

            // Assert: the session left the server once, whatever its disposal threw.
            recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("Terminated");
        }

        SqlDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static string UniqueEngineName() => "sql-events-" + Guid.NewGuid().ToString("N");

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
