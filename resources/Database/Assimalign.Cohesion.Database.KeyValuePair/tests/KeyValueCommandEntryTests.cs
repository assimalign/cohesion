using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Testing;

using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

[Collection(nameof(KeyValueDatabaseEventSourceCollection))]
public sealed class KeyValueCommandEntryTests
{
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Command entry: Should retain validation and refusal precedence before parsing")]
    [InlineData("Closed")]
    [InlineData("Blank")]
    [InlineData("Canceled")]
    [InlineData("Offline")]
    [InlineData("Transaction")]
    [InlineData("Parse")]
    public async Task ExecuteCommandTextAsync_CompetingRefusals_ShouldPreserveTheirOrder(string first)
    {
        // Arrange — each earlier refusal competes with the later invalid grammar.
        await using var engine = KeyValueDatabaseEngine.Create("command-order", new KeyValueDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("kv", TestTimeout.Token());
        await using var session = await database.CreateSessionAsync(TestTimeout.Token());
        if (first is "Offline" or "Transaction")
        {
            var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
            await database.Coordinator.RollbackAsync(transaction.Context, TestTimeout.Token());
        }
        if (first == "Closed")
        {
            await session.DisposeAsync();
        }
        if (first is "Closed" or "Blank" or "Canceled" or "Offline")
        {
            database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "test offline", new IOException("test failure")).ShouldBeTrue();
        }
        using var cancellation = new CancellationTokenSource();
        if (first is "Closed" or "Blank" or "Canceled")
        {
            cancellation.Cancel();
        }
        string text = first is "Closed" or "Blank" ? " " : "FROB @k";

        // Act
        Exception error = await Should.ThrowAsync<Exception>(async () =>
            await session.ExecuteCommandTextAsync(text, cancellationToken: cancellation.Token));

        // Assert
        switch (first)
        {
            case "Closed":
                error.ShouldBeOfType<DatabaseException>().Message.ShouldContain("closed", Case.Insensitive);
                break;
            case "Blank":
                error.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("commandText");
                break;
            case "Canceled":
                error.ShouldBeAssignableTo<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
                break;
            case "Offline":
                error.ShouldBeOfType<DatabaseOfflineException>().Code.ShouldBe("CV06102");
                break;
            case "Transaction":
                error.ShouldBeAssignableTo<DatabaseException>().Diagnostics[0].Code.ShouldBe("CV04101");
                break;
            case "Parse":
                error.ShouldBeOfType<DatabaseParseException>();
                break;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire commands: Should decode parameters then refuse state before parsing")]
    [InlineData(true, true, true, ProtocolErrorCode.ProtocolViolation)]
    [InlineData(false, true, true, ProtocolErrorCode.Unavailable)]
    [InlineData(false, false, true, ProtocolErrorCode.ExecutionFailure)]
    [InlineData(false, false, false, ProtocolErrorCode.ParseFailure)]
    public async Task Execute_WireRefusal_ShouldPreserveTheCheckAndReportingOrder(bool malformed, bool offline, bool refusing, ProtocolErrorCode expected)
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        using var events = new EventSourceRecorder(KeyValueDatabaseEventSource.Log, EventLevel.Verbose);
        using var root = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database"), EventLevel.Verbose);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var session = harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession.ShouldBeOfType<KeyValueDatabaseSession>();
        if (refusing)
        {
            var transaction = await session.BeginTransactionAsync(TestTimeout.Token());
            await session.Database.Coordinator.RollbackAsync(transaction.Context, TestTimeout.Token());
        }
        if (offline)
        {
            session.Database.DataStorage.TakeOffline(StorageOfflineCause.CheckpointFailures, "test offline", new IOException("test failure")).ShouldBeTrue();
        }
        long failuresBefore = KeyValueDatabaseEventSource.Log.TotalStatementParseFailures;
        var parameters = new Dictionary<string, byte[]>();
        if (malformed)
        {
            parameters.Add("k", []);
        }

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage("FROB @k", parameters).Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);

        // Assert — all four stop before the typed NVI, so no root statement is reported.
        error.Code.ShouldBe(expected);
        KeyValueDatabaseEventSource.Log.TotalStatementParseFailures.ShouldBe(failuresBefore + (expected == ProtocolErrorCode.ParseFailure ? 1 : 0));
        root.Events.ShouldNotContain(e => e.EventId == 25 || e.EventId == 28);
        events.Events.ShouldNotContain(e => e.EventId == 0);
        if (malformed)
        {
            (await client.ReadAsync()).ShouldBeNull();
        }
        else
        {
            await client.SendAsync(ProtocolMessageType.Ping);
            await client.ExpectAsync(ProtocolMessageType.Pong);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Wire commands: Should parse operands and report the typed request")]
    public async Task Execute_ValidWireCommand_ShouldReachTheTypedNvi()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        using var root = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database"), EventLevel.Verbose);
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        var parameters = new Dictionary<string, byte[]> { ["k"] = DatabaseValueCodec.EncodeComponent(new byte[] { 42 }) };

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, new ProtocolExecuteMessage("GET @k", parameters).Encode());
        await client.ExpectAsync((ProtocolMessageType)KeyValueProtocolMessageType.ResultHeader);
        await client.ExpectAsync((ProtocolMessageType)KeyValueProtocolMessageType.ResultComplete);

        // Assert — GET of an absent key returns an empty set and one typed trace.
        root.Events.Where(e => e.EventId == 25).ShouldHaveSingleItem().Payload![2].ShouldBe(nameof(KeyValueGetRequest));
        root.Events.Where(e => e.EventId == 26).ShouldHaveSingleItem().Payload![2].ShouldBe("Success");
        root.Events.ShouldNotContain(e => e.EventId == 0 || e.EventId == 28);
    }
}
