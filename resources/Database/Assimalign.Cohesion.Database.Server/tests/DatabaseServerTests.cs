using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Server.Tests;

/// <summary>
/// The shared server core, proven model-independent: every test drives the
/// machinery (session state machine, version negotiation, guardrails, the
/// execute pump, two-phase drain) through a <b>fake</b> model engine — no SQL,
/// no key-value, just the root contracts. Moved/adapted from the SQL server
/// suite when the machinery was extracted (2026-07-14, the second model server);
/// the model suites keep their model-specific wire behaviors.
/// </summary>
public class DatabaseServerTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Server] - Handshake: startup/authenticate/ready reaches an authenticated session")]
    public async Task Handshake_WithCurrentVersion_ShouldReachReady()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.HandshakeAsync(principal: "ada");

        // Assert
        var session = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        session.Principal.ShouldBe("ada");
        session.ProtocolVersion.ShouldBe(ProtocolVersion.Current);
        session.DatabaseSession.ShouldNotBeNull();
        harness.Server.Context.Engine.ShouldBeSameAs(harness.Engine);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Handshake: unknown protocol major is rejected with UnsupportedVersion")]
    public async Task Handshake_WithUnknownMajorVersion_ShouldRejectWithUnsupportedVersion()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(new ProtocolVersion(99, 0), ServerTestHarness.DatabaseName, "tester").Encode());

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.UnsupportedVersion);

        (await client.ReadAsync()).ShouldBeNull(); // the server closed the connection
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Handshake: unknown database is rejected with DatabaseNotFound")]
    public async Task Handshake_WithUnknownDatabase_ShouldRejectWithDatabaseNotFound()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "no-such-db", "tester").Encode());

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Handshake: a rejecting authenticator yields AuthenticationFailed")]
    public async Task Handshake_WithRejectingAuthenticator_ShouldFailAuthentication()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.Authenticator = new RejectingAuthenticator());
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, ServerTestHarness.DatabaseName, "mallory").Encode());
        await client.ExpectAsync(ProtocolMessageType.Authenticate);
        await client.SendAsync(ProtocolMessageType.AuthenticateResponse);

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.AuthenticationFailed);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Execute pump: a row-returning result streams ResultHeader, rows, and ResultComplete")]
    public async Task Execute_RowReturningStatement_ShouldStreamHeaderRowsAndComplete()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("rows").Encode());

        // Assert: header carries the shared type identities
        var headerFrame = await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var header = ProtocolResultHeaderMessage.Decode(headerFrame.Payload.Span);
        header.Columns.Count.ShouldBe(2);
        header.Columns[0].ShouldBe(("name", (byte)DatabaseType.String));
        header.Columns[1].ShouldBe(("value", (byte)DatabaseType.Int32));

        // Rows are raw tuple-codec components, one per column
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray()).ShouldBe(["a", 1]);
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray()).ShouldBe(["b", 2]);

        var completeFrame = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(completeFrame.Payload.Span).AffectedCount.ShouldBe(-1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Execute pump: statement results return the affected count")]
    public async Task Execute_PlainResultStatement_ShouldReturnAffectedCount()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("count").Encode());

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(frame.Payload.Span).AffectedCount.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Execute pump: wire parameters decode and bind by bare name")]
    public async Task Execute_WithParameters_ShouldBindDecodedValues()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        var message = new ProtocolExecuteMessage(
            "echo",
            new Dictionary<string, byte[]> { ["value"] = DatabaseValueCodec.EncodeComponent(42L) });

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, message.Encode());

        // Assert
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray()).ShouldBe([42L]);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Execute pump: parse failures report ParseFailure and keep the session alive")]
    public async Task Execute_ParseError_ShouldReportParseFailureAndKeepSessionAlive()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("parse-error").Encode());

        // Assert: parse failure, then the session still executes
        var errorFrame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(errorFrame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ParseFailure);

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("count").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Execute pump: execution failures report ExecutionFailure and keep the session alive")]
    public async Task Execute_ExecutionError_ShouldReportExecutionFailureAndKeepSessionAlive()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("exec-error").Encode());

        // Assert
        var errorFrame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(errorFrame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);

        harness.Server.Context.Sessions.ShouldHaveSingleItem();

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("count").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Guardrails: connections beyond MaxSessions are rejected with Unavailable")]
    public async Task Accept_BeyondMaxSessions_ShouldRejectWithUnavailable()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.MaxSessions = 1);
        await using var first = await harness.DialAsync();
        await first.HandshakeAsync();

        // Act
        await using var second = await harness.DialAsync();

        // Assert: the second connection is rejected before any handshake
        var frame = await second.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        (await second.ReadAsync()).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Guardrails: unauthenticated connections are dropped after the authentication timeout")]
    public async Task Handshake_WhenAuthenticationTimesOut_ShouldDropConnection()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200));
        await using var client = await harness.DialAsync();

        // Act: send nothing and wait for the server to give up
        var frame = await client.ReadAsync();

        // Assert
        frame.ShouldBeNull();
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Context.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Guardrails: idle sessions are evicted after the idle timeout")]
    public async Task ReadyLoop_WhenIdleTimeoutLapses_ShouldEvictSession()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.IdleTimeout = TimeSpan.FromMilliseconds(200));
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: go idle
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);

        // Assert
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        (await client.ReadAsync()).ShouldBeNull();
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Context.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Liveness: ping frames answer with pong")]
    public async Task ReadyLoop_OnPing_ShouldAnswerPong()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Ping);

        // Assert
        await client.ExpectAsync(ProtocolMessageType.Pong);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Lifecycle: terminate closes the session cleanly")]
    public async Task ReadyLoop_OnTerminate_ShouldCloseSession()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Terminate);

        // Assert
        (await client.ReadAsync()).ShouldBeNull();
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Context.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Lifecycle: StopAsync drains idle sessions within the budget")]
    public async Task StopAsync_WithIdleSessions_ShouldDrainGracefully()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.ShutdownDrainTimeout = TimeSpan.FromSeconds(10));
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await harness.Server.StopAsync(TestTimeout.Token(30));
        stopwatch.Stop();

        // Assert: the drain closed the idle session at the frame boundary — far
        // inside the budget — and told the client why.
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        harness.Server.Context.Sessions.ShouldBeEmpty();

        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        (await client.ReadAsync()).ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Server] - Options: defaults are bounded and creation validates its inputs")]
    public async Task Create_WithInvalidOptions_ShouldThrow()
    {
        // Arrange
        var defaults = new DatabaseServerOptions();
        defaults.MaxSessions.ShouldBeGreaterThan(0);
        defaults.AuthenticationTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        defaults.IdleTimeout.ShouldBeGreaterThan(TimeSpan.Zero);
        defaults.ShutdownDrainTimeout.ShouldBeGreaterThan(TimeSpan.Zero);

        await using var engine = new FakeModelEngine();

        // Act / Assert: no listener.
        Should.Throw<ArgumentException>(() => new TestDatabaseServer(engine, new DatabaseServerOptions()));

        // Act / Assert: non-positive session limit.
        await using var listener = new InMemoryConnectionListener();
        Should.Throw<ArgumentException>(() => new TestDatabaseServer(engine, new DatabaseServerOptions { Listener = listener, MaxSessions = 0 }));

        // Act / Assert: null engine.
        Should.Throw<ArgumentNullException>(() => new TestDatabaseServer(null!, new DatabaseServerOptions { Listener = listener }));
    }

    private static object?[] DecodeRow(byte[] payload)
    {
        var values = new List<object?>();
        var reader = new DatabaseKeyReader(payload);

        while (!reader.IsAtEnd)
        {
            values.Add(DatabaseValueCodec.Read(ref reader));
        }

        return [.. values];
    }
}
