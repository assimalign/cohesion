using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The SQL model's wire behavior over a live engine: the handshake smoke and the
/// SQL execute exchange (statements, parameters, the parse/execution error
/// taxonomy) — all over the in-memory Connections driver, through
/// <see cref="SqlDatabaseServer"/> directly. The model-agnostic machinery
/// (guardrails, drain, the generic pump) is covered by the shared core's own
/// suite in <c>Assimalign.Cohesion.Database.Server</c>, where the tests moved
/// when the machinery was extracted (2026-07-14, the second model server).
/// </summary>
public class SqlDatabaseServerTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server handshake: startup/authenticate/ready reaches an authenticated session")]
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
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server execute: SELECT streams ResultHeader, rows, and ResultComplete")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server execute: statement results return the affected count")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server execute: wire parameters decode and bind by bare name")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server execute: parse failures report ParseFailure and keep the session alive")]
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

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Server execute: execution failures report ExecutionFailure and keep the session alive")]
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

        harness.Server.Context.Sessions.ShouldHaveSingleItem();

        await client.SendAsync(ProtocolMessageType.Execute, ProtocolExecuteMessage.Create("SELECT id FROM users WHERE id = 1").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
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
