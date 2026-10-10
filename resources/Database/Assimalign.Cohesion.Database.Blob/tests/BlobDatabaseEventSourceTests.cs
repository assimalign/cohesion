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

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Blob.Internal;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob.Tests;

/// <summary>
/// Serializes the tests that observe the blob model's event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(BlobDatabaseEventSourceCollection), DisableParallelization = true)]
public class BlobDatabaseEventSourceCollection
{
}

/// <summary>
/// The blob model's event source against the repository's EventSource convention, and the server
/// events it writes for real sessions over the in-memory driver: each event once, with its
/// declared payload, and the server-session gauge back where it started (database event-sources
/// plan, section 4.9). The SQL, Key-value and Graph suites check the same ids, names and payloads
/// against their own sources. The Blob server's own events follow: a database or engine it
/// refuses, and an exchange that fails.
/// </summary>
[Collection(nameof(BlobDatabaseEventSourceCollection))]
public sealed class BlobDatabaseEventSourceTests
{
    private static readonly string[] SessionClosedPayload = ["sessionId", "reason", "durationMilliseconds"];

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should be named for its assembly")]
    public void GetName_BlobDatabaseEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(BlobDatabaseEventSource));

        // Assert
        name.ShouldBe(typeof(BlobDatabaseEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Blob");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(BlobDatabaseEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Blob", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report an accepted session and its close once each and restore the gauge")]
    public async Task SessionLifecycle_AcceptAndTerminate_ShouldReportEachOnceAndRestoreTheGauge()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        long currentBefore = BlobDatabaseEventSource.Log.CurrentServerSessions;
        long totalBefore = BlobDatabaseEventSource.Log.TotalServerSessions;
        long currentWhileOpen;

        // Act
        await using (var harness = await BlobServerHarness.StartAsync(engineName: engineName))
        {
            await using var client = await harness.DialAsync();
            await client.HandshakeAsync();
            currentWhileOpen = BlobDatabaseEventSource.Log.CurrentServerSessions;
            await client.SendAsync(ProtocolMessageType.Terminate);
            (await client.ReadAsync()).ShouldBeNull();
            await BlobServerHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
        }

        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        var closed = await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert
        currentWhileOpen.ShouldBe(currentBefore + 1);
        BlobDatabaseEventSource.Log.CurrentServerSessions.ShouldBe(currentBefore);
        BlobDatabaseEventSource.Log.TotalServerSessions.ShouldBe(totalBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        var accepted = recorder.Events.Where(e => e.EventId == 1 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem();
        accepted.EventName.ShouldBe("SessionAccepted");
        accepted.Level.ShouldBe(EventLevel.Verbose);
        (accepted.Keywords & BlobDatabaseEventSource.Keywords.Sessions).ShouldBe(BlobDatabaseEventSource.Keywords.Sessions);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a connection beyond the session limit as rejected once")]
    public async Task SessionRejected_BeyondMaxSessions_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = BlobDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await BlobServerHarness.StartAsync(options => options.MaxSessions = 1, engineName: engineName);
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
        BlobDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report each handshake refusal once with its code")]
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
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(
            options => options.Authenticator = refusal == "AuthenticationFailed" ? new RejectingAuthenticator() : null,
            engineName: engineName);
        if (refusal == "DatabaseOffline")
        {
            var open = await harness.Engine.OpenDatabaseAsync(BlobServerHarness.DatabaseName);
            open.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure")).ShouldBeTrue();
        }

        await using var client = await harness.DialAsync();
        string database = refusal == "DatabaseNotFound" ? "no-such-db" : BlobServerHarness.DatabaseName;
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a handshake that outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_SilentPeer_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a handshake whose authenticator outlives the authentication timeout once")]
    public async Task HandshakeTimedOut_StallingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange: the timeout lapses inside the authenticator, not at a read.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = new StallingAuthenticator();
                options.AuthenticationTimeout = TimeSpan.FromSeconds(2);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, BlobServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should cut the names an unauthenticated peer sent to their bound in a handshake refusal")]
    public async Task HandshakeRefused_OversizedStartup_ShouldWriteBoundedNames()
    {
        // Arrange: a startup refused before any lookup, naming a database and principal far past
        // the 256-character payload bound.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(engineName: engineName);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a session the idle timeout evicts as closed once")]
    public async Task SessionClosed_IdleTimeout_ShouldBeReportedOnceWithItsReason()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200), engineName: engineName);
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

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a protocol violation once and the session closed for it")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionProtocolViolation_ReadyState_ShouldBeReportedOnce(bool uploadWithoutTransferStart)
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a frame the ready state does not take, or an upload whose content does not begin
        // with TransferStart.
        if (uploadWithoutTransferStart)
        {
            await client.SendAsync(BlobProtocolMessageType.Write, new BlobWriteMessage(BlobServerHarness.ContainerName, "item").Encode());
            await client.SendAsync(ProtocolMessageType.Ping);
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report an unexpected fault in the exchange as an error once")]
    public async Task SessionFaulted_ThrowingAuthenticator_ShouldBeReportedOnce()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(options => options.Authenticator = new FaultingAuthenticator(), engineName: engineName);
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, BlobServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report the sessions a lapsed drain aborts once")]
    public async Task SessionsAborted_DrainTimesOut_ShouldBeReportedOnce()
    {
        // Arrange: a session held inside its handshake, which the soft stop cannot end.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        var authenticator = new BlockingAuthenticator();
        await using var harness = await BlobServerHarness.StartAsync(
            options =>
            {
                options.Authenticator = authenticator;
                options.ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100);
            },
            engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, BlobServerHarness.DatabaseName, "ada").Encode());
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

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should publish its server-session counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishServerSessionCounters()
    {
        // Arrange
        string[] counters = ["current-server-sessions", "total-server-sessions", "total-rejected-sessions"];

        // Act
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, TestTimeout.Token(30));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should allocate nothing on any write while nobody listens")]
    public async Task Writes_NoListener_ShouldAllocateNothing()
    {
        // Arrange: a disabled source. Disposing the last listener already disables it; the explicit
        // disable below is redundant but harmless.
        await using var engine = BlobDatabaseEngine.Create(UniqueEngineName(), new BlobDatabaseEngineOptions());
        var session = new StubServerSession();
        var failure = new InvalidOperationException("failure");
        using (var listener = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose))
        {
            listener.DisableEvents(BlobDatabaseEventSource.Log);
        }

        BlobDatabaseEventSource.Log.IsEnabled().ShouldBeFalse();
        WriteEveryEvent(engine, session, failure);

        // Act: the fewest bytes of three rounds. A write that allocates does so in every round, while
        // the runtime can now and then charge an allocation of its own to this thread.
        long allocated = long.MaxValue;
        long timestamp = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            timestamp |= WriteEveryEvent(engine, session, failure);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert: no round read a timestamp.
        allocated.ShouldBe(0);
        timestamp.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a database a failing worker refuses once per refusal, at the handshake and at an exchange")]
    public async Task DatabaseRefused_WorkerFailing_ShouldBeReportedAtTheHandshakeAndAtAnExchange()
    {
        // Arrange: a checkpoint worker the test drives pass by pass fails database "failing" while
        // its failure is set; the engine's give-up is out of reach (owner decision 42).
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        DatabaseFailingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder(engineName);
        builder.Options.WorkerFailureMinimumPasses = int.MaxValue;
        builder.AddWorker(built => registered = new DatabaseFailingWorker(built.Name + "/probe", "failing"));
        var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var failing = await engine.CreateDatabaseAsync("failing", TestTimeout.Token(30));
        await AutocommitContainer.CreateAsync(failing, BlobServerHarness.ContainerName, TestTimeout.Token(30));
        await using var harness = await BlobServerHarness.StartAsync(engine);

        // Act: the handshake is refused while the worker fails; once a pass finishes the work, a
        // session opens; the worker fails again and the session's next exchange is refused.
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(TestTimeout.Token(30));
        await using var refused = await harness.DialAsync();
        await refused.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "failing", "ada").Encode());
        await refused.ExpectAsync(ProtocolMessageType.Authenticate);
        await refused.SendAsync(ProtocolMessageType.AuthenticateResponse);
        var handshakeRefusal = ProtocolErrorMessage.Decode((await refused.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        Guid refusedSession = await AcceptedSessionAsync(recorder, engineName);

        worker.Failure = null;
        worker.RunIteration(TestTimeout.Token(30));
        await using var served = await harness.DialAsync();
        await served.HandshakeAsync(database: "failing");
        Guid servedSession = harness.Server.Sessions.Single(session => session.DatabaseSession is not null).Id;
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(TestTimeout.Token(30));
        await served.SendAsync(BlobProtocolMessageType.List, new BlobListMessage(BlobServerHarness.ContainerName, "").Encode());
        var exchangeRefusal = ProtocolErrorMessage.Decode((await served.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await recorder.WaitForAsync(e => IsFor(e, 5, refusedSession), TestTimeout.Token(30));
        await recorder.WaitForAsync(e => IsFor(e, 5, servedSession), TestTimeout.Token(30));

        // Assert
        handshakeRefusal.Message.ShouldStartWith("COHDBB003", Case.Sensitive);
        exchangeRefusal.Message.ShouldStartWith("COHDBB003", Case.Sensitive);
        var atHandshake = recorder.Events.Where(e => IsFor(e, 10, refusedSession)).ShouldHaveSingleItem();
        atHandshake.EventName.ShouldBe("DatabaseRefused");
        atHandshake.Level.ShouldBe(EventLevel.Warning);
        atHandshake.PayloadNames.ShouldBe(["sessionId", "database", "phase", "detail"]);
        atHandshake.Payload.ShouldBe([refusedSession, "failing", "Handshake", handshakeRefusal.Message]);
        recorder.Events.Where(e => IsFor(e, 3, refusedSession)).ShouldHaveSingleItem().Payload![3].ShouldBe(nameof(ProtocolErrorCode.Unavailable));
        recorder.Events.Where(e => IsFor(e, 5, refusedSession)).ShouldHaveSingleItem().Payload![1].ShouldBe("HandshakeRefused");
        var atExchange = recorder.Events.Where(e => IsFor(e, 10, servedSession)).ShouldHaveSingleItem();
        atExchange.Payload.ShouldBe([servedSession, "failing", "Exchange", exchangeRefusal.Message]);
        recorder.Events.Where(e => IsFor(e, 5, servedSession)).ShouldHaveSingleItem().Payload![1].ShouldBe("ExchangeRefused");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a disposed engine's refusals once each, at the handshake and at the accept")]
    public async Task EngineRefused_DisposedEngine_ShouldBeReportedAtTheHandshakeAndAtTheAccept()
    {
        // Arrange: a session accepted while the engine ran, then the engine disposed under the
        // running server.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        long rejectedBefore = BlobDatabaseEventSource.Log.TotalRejectedSessions;
        await using var harness = await BlobServerHarness.StartAsync(engineName: engineName);
        await using var accepted = await harness.DialAsync();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await harness.Engine.DisposeAsync();

        // Act
        await accepted.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, BlobServerHarness.DatabaseName, "ada").Encode());
        var handshakeRefusal = ProtocolErrorMessage.Decode((await accepted.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));
        await using var rejected = await harness.DialAsync();
        var acceptRefusal = ProtocolErrorMessage.Decode((await rejected.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        await recorder.WaitForAsync(e => e.EventId == 2 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));

        // Assert
        handshakeRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        acceptRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        var refusals = recorder.Events.Where(e => e.EventId == 11 && Equals(e.Payload![0], engineName)).ToArray();
        refusals.Length.ShouldBe(2);
        refusals[0].EventName.ShouldBe("EngineRefused");
        refusals[0].Level.ShouldBe(EventLevel.Warning);
        refusals[0].PayloadNames.ShouldBe(["engineName", "phase", "state"]);
        refusals[0].Payload.ShouldBe([engineName, "Handshake", nameof(EngineState.Disposed)]);
        refusals[1].Payload.ShouldBe([engineName, "Accept", nameof(EngineState.Disposed)]);
        recorder.Events.Where(e => IsFor(e, 3, sessionId)).ShouldHaveSingleItem().Payload![4].ShouldBe(handshakeRefusal.Message);
        recorder.Events.Where(e => e.EventId == 2 && Equals(e.Payload![0], engineName)).ShouldHaveSingleItem()
            .Payload.ShouldBe([engineName, "EngineRefused", 0, 100]);
        BlobDatabaseEventSource.Log.TotalRejectedSessions.ShouldBe(rejectedBefore + 1);
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should report a failed exchange once with its container, code and type and no blob name, and the session closed for it")]
    public async Task TransferFailed_MissingBlob_ShouldBeReportedOnceWithItsContainerAndNoBlobName()
    {
        // Arrange
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        await using var harness = await BlobServerHarness.StartAsync(engineName: engineName);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a read of a blob the container does not hold.
        await client.SendAsync(BlobProtocolMessageType.Read, new BlobReadMessage(BlobServerHarness.ContainerName, "missing").Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        (await client.ReadAsync()).ShouldBeNull();
        Guid sessionId = await AcceptedSessionAsync(recorder, engineName);
        await recorder.WaitForAsync(e => IsFor(e, 5, sessionId), TestTimeout.Token(30));

        // Assert: the client gets the engine's message; the event names the container but not the
        // blob, which may be user data (plan D8, Q3).
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldBe("Blob 'missing' does not exist.");
        var failed = recorder.Events.Where(e => IsFor(e, 13, sessionId)).ShouldHaveSingleItem();
        failed.EventName.ShouldBe("TransferFailed");
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["sessionId", "container", "code", "exceptionType"]);
        failed.Payload.ShouldBe([sessionId, BlobServerHarness.ContainerName, nameof(ProtocolErrorCode.ExecutionFailure), typeof(DatabaseException).FullName]);
        recorder.Events.Where(e => IsForSession(e, sessionId)).SelectMany(e => e.Payload!).OfType<string>().ShouldNotContain(text => text.Contains("missing", StringComparison.Ordinal));
        recorder.Events.Where(e => IsForSession(e, sessionId)).Select(e => e.EventId).ShouldBe([13, 5]);
        recorder.Events.Where(e => IsFor(e, 5, sessionId)).ShouldHaveSingleItem().Payload![1].ShouldBe("ExchangeFailed");
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should write a transfer failure without its message and bound the container")]
    public void TransferFailed_BlobNamedInMessage_ShouldWriteNoMessageAndBoundTheContainer()
    {
        // Arrange
        var session = new StubServerSession();
        var failure = new DatabaseException("Blob 'q3/ledger.csv' already exists; 'q3/ledger.csv' was not replaced.");
        string container = new('c', 1_000);
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);

        // Act
        BlobDatabaseEventSource.Log.TransferFailed(session, container, ProtocolErrorCode.ExecutionFailure, failure);

        // Assert
        var failed = recorder.Events.Where(e => e.EventId == 13 && Equals(e.Payload![0], session.Id)).ShouldHaveSingleItem();
        failed.Payload.ShouldBe([session.Id, new string('c', 256) + "...", nameof(ProtocolErrorCode.ExecutionFailure), typeof(DatabaseException).FullName]);
        failed.Payload!.OfType<string>().ShouldNotContain(text => text.Contains("ledger", StringComparison.Ordinal));
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }

    private static long WriteEveryEvent(DatabaseEngine engine, DatabaseServerSession session, Exception failure)
    {
        var log = BlobDatabaseEventSource.Log;
        log.DatabaseRefused(session, new DatabaseName("objects"), BlobDatabaseEventSource.RefusalPhase.Exchange, "refused");
        log.EngineRefused(engine, BlobDatabaseEventSource.RefusalPhase.Accept, EngineState.Disposed);
        log.HostTransactionAbortFailed(session, failure);
        log.TransferFailed(session, "files", ProtocolErrorCode.ExecutionFailure, failure);
        log.TransferFailed(null, string.Empty, code: null, failure);
        long timestamp = log.SessionTimestamp();
        log.SessionAccepted(engine, session, 1);
        log.SessionRejected(engine, BlobDatabaseEventSource.RejectReason.SessionLimit, 1, 1);
        log.HandshakeRefused(session, "objects", "ada", ProtocolErrorCode.AuthenticationFailed, "refused");
        log.HandshakeTimedOut(session, TimeSpan.FromSeconds(1));
        log.SessionProtocolViolation(session, "violation");
        log.SessionFaulted(session, failure);
        log.SessionCleanupFailed(session, failure);
        log.SessionClosed(session, BlobDatabaseEventSource.CloseReason.Terminated, timestamp);
        log.SessionsAborted(engine, 1, TimeSpan.FromSeconds(1));
        return timestamp;
    }

    [Theory(DisplayName = "Cohesion Test [Database.Blob] - BlobDatabaseEventSource: Should close a session whose peer reset its TCP connection with a transport reason, not a fault")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionClosed_PeerResetsTcpConnection_ShouldReportATransportReasonAndNoFault(bool whileServerWrites)
    {
        // Arrange: a real TCP listener; the in-memory driver cannot reset a connection.
        string engineName = UniqueEngineName();
        using var recorder = new EventSourceRecorder(BlobDatabaseEventSource.Log, EventLevel.Verbose);
        TcpConnectionListener listener = TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0));
        await using var harness = await BlobServerHarness.StartAsync(options => options.Listener = listener, engineName: engineName);

        // Act: the peer completes its handshake, then resets the connection: while the session idles
        // in its ready loop, or while the server's pong writes fill the socket buffers the peer
        // never drains, so the reset fails a send the pump is blocked in.
        using (Socket socket = await TcpResetPeer.ConnectReadyAsync(listener.EndPoint, BlobServerHarness.DatabaseName, TestTimeout.Token()))
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

    private static string UniqueEngineName() => "blob-events-" + Guid.NewGuid().ToString("N");

    private static async Task<Guid> AcceptedSessionAsync(EventSourceRecorder recorder, string engineName)
    {
        var accepted = await recorder.WaitForAsync(e => e.EventId == 1 && Equals(e.Payload![0], engineName), TestTimeout.Token(30));
        return (Guid)accepted.Payload![1]!;
    }

    // The session events whose first payload field is the session's id.
    private static bool IsForSession(EventWrittenEventArgs written, Guid sessionId)
        => written.EventId is (>= 3 and <= 8) or 10 or 12 or 13 && written.Payload is { Count: > 0 } payload && Equals(payload[0], sessionId);

    private static bool IsFor(EventWrittenEventArgs written, int eventId, Guid sessionId)
        => written.EventId == eventId && IsForSession(written, sessionId);

    /// <summary>
    /// A checkpoint worker the test drives pass by pass: each pass reports <see cref="Failure"/>
    /// for one database while it is set, with no backoff, and finishes that database's work
    /// otherwise. Its one-hour interval keeps the engine's pump from running a pass the test did
    /// not ask for.
    /// </summary>
    private sealed class DatabaseFailingWorker : DatabaseEngineWorker
    {
        private readonly string _database;
        private Exception? _failure;

        public DatabaseFailingWorker(string name, string database)
            : base(name, DatabaseEngineWorkerKind.Checkpoint, TimeSpan.FromHours(1))
        {
            _database = database;
        }

        public Exception? Failure
        {
            get => Volatile.Read(ref _failure);
            set => Volatile.Write(ref _failure, value);
        }

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (BeginDatabase(_database) && Failure is { } failure)
            {
                ReportFailure(_database, failure, TimeSpan.Zero);
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
