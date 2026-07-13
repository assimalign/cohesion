using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

/// <summary>
/// End-to-end tests for the server runtime (#852): the session state machine,
/// version negotiation, the execute exchange against a live SQL engine, and the
/// DoS guardrails — all over the in-memory Connections driver.
/// </summary>
public class DatabaseServerTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Handshake: startup/authenticate/ready reaches an authenticated session")]
    public async Task Handshake_WithCurrentVersion_ShouldReachReady()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.HandshakeAsync(principal: "ada");

        // Assert
        var session = harness.Server.Sessions.ShouldHaveSingleItem();
        session.Principal.ShouldBe("ada");
        session.ProtocolVersion.ShouldBe(ProtocolVersion.Current);
        session.DatabaseSession.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Handshake: unknown protocol major is rejected with UnsupportedVersion")]
    public async Task Handshake_WithUnknownMajorVersion_ShouldRejectWithUnsupportedVersion()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(new ProtocolVersion(99, 0), ServerTestHarness.DatabaseName, "tester").Encode());

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.UnsupportedVersion);

        (await client.ReadAsync()).ShouldBeNull(); // the server closed the connection
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Handshake: unknown database is rejected with DatabaseNotFound")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Handshake: a rejecting authenticator yields AuthenticationFailed")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Execute: SELECT streams ResultHeader, rows, and ResultComplete")]
    public async Task Execute_SelectStatement_ShouldStreamHeaderRowsAndComplete()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id, name FROM users ORDER BY id").Encode());

        // Assert: header carries the shared type identities
        var headerFrame = await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var header = ProtocolResultHeaderMessage.Decode(headerFrame.Payload.Span);
        header.Columns.Count.ShouldBe(2);
        header.Columns[0].ShouldBe(("id", (byte)DatabaseType.Int32));
        header.Columns[1].ShouldBe(("name", (byte)DatabaseType.String));

        // Rows are raw tuple-codec components, one per column
        var firstRow = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        DecodeRow(firstRow.Payload.ToArray()).ShouldBe([1, "ada"]);

        var secondRow = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        DecodeRow(secondRow.Payload.ToArray()).ShouldBe([2, "grace"]);

        var completeFrame = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(completeFrame.Payload.Span).AffectedCount.ShouldBe(-1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Execute: statement results return the affected count")]
    public async Task Execute_InsertStatement_ShouldReturnAffectedCount()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("INSERT INTO users (id, name) VALUES (3, 'lin')").Encode());

        // Assert
        var frame = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(frame.Payload.Span).AffectedCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Execute: wire parameters decode and bind by bare name")]
    public async Task Execute_WithParameters_ShouldBindDecodedValues()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        var message = new ProtocolExecuteMessage(
            "SELECT name FROM users WHERE id = @id",
            new Dictionary<string, byte[]> { ["id"] = DatabaseValueCodec.EncodeComponent(2) });

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, message.Encode());

        // Assert
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var row = await client.ExpectAsync(ProtocolMessageType.ResultRow);
        DecodeRow(row.Payload.ToArray()).ShouldBe(["grace"]);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Execute: parse failures report ParseFailure and keep the session alive")]
    public async Task Execute_InvalidSql_ShouldReportParseFailureAndKeepSessionAlive()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: a command outside the declared dialect (the parser is total — only
        // unknown commands and empty input carry error-severity diagnostics)
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("TRUNCATE TABLE users;").Encode());

        // Assert: parse failure, then the session still executes
        var errorFrame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(errorFrame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ParseFailure);

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users WHERE id = 1").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        await client.ExpectAsync(ProtocolMessageType.ResultRow);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Execute: execution failures report ExecutionFailure and keep the session alive")]
    public async Task Execute_UnknownTable_ShouldReportExecutionFailureAndKeepSessionAlive()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM missing_table").Encode());

        // Assert
        var errorFrame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(errorFrame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);

        harness.Server.Sessions.ShouldHaveSingleItem();

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users WHERE id = 1").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Guardrails: connections beyond MaxSessions are rejected with Unavailable")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Guardrails: unauthenticated connections are dropped after the authentication timeout")]
    public async Task Handshake_WhenAuthenticationTimesOut_ShouldDropConnection()
    {
        // Arrange
        await using var harness = await ServerTestHarness.StartAsync(options => options.AuthenticationTimeout = TimeSpan.FromMilliseconds(200));
        await using var client = await harness.DialAsync();

        // Act: send nothing and wait for the server to give up
        var frame = await client.ReadAsync();

        // Assert
        frame.ShouldBeNull();
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Guardrails: idle sessions are evicted after the idle timeout")]
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
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Liveness: ping frames answer with pong")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: terminate closes the session cleanly")]
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
        await ServerTestHarness.WaitUntilAsync(() => harness.Server.Sessions.Count == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Hosting] - Lifecycle: StopAsync drains idle sessions within the budget")]
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
        harness.Server.Sessions.ShouldBeEmpty();

        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        (await client.ReadAsync()).ShouldBeNull();
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
