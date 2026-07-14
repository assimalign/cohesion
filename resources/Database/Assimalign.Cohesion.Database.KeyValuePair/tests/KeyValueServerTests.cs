using System.Collections.Generic;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// The key-value model's wire behavior over a live engine: the second model
/// server rides the shared core and the existing protocol unchanged — command
/// text + named tuple-codec parameters in, generic result framing out.
/// </summary>
public class KeyValueServerTests
{
    private static ProtocolExecuteMessage Command(string text, params (string Name, object? Value)[] parameters)
    {
        var encoded = new Dictionary<string, byte[]>(parameters.Length);

        foreach (var (name, value) in parameters)
        {
            encoded[name] = DatabaseValueCodec.EncodeComponent(value);
        }

        return new ProtocolExecuteMessage(text, encoded);
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

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server handshake: startup/authenticate/ready reaches an authenticated session")]
    public async Task Handshake_WithCurrentVersion_ShouldReachReady()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();

        // Act
        await client.HandshakeAsync(principal: "ada");

        // Assert
        var session = harness.Server.Context.Sessions.ShouldHaveSingleItem();
        session.Principal.ShouldBe("ada");
        session.DatabaseSession.ShouldNotBeNull();
        harness.Server.Context.Engine.ShouldBeSameAs(harness.Engine);
        harness.Server.Engine.Model.ShouldBe(EngineModel.KeyValueStore);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server execute: PUT then GET round-trips over the wire")]
    public async Task Execute_PutThenGet_ShouldRoundTripOverWire()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act: PUT — a one-row outcome set [applied, etag].
        await client.SendAsync(ProtocolMessageType.Execute,
            Command("PUT @k @v", ("k", Bytes("user:1")), ("v", Bytes("ada"))).Encode());

        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var putRow = DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray());
        var putComplete = await client.ExpectAsync(ProtocolMessageType.ResultComplete);

        // Assert: applied, with the outcome set's affected count on the wire.
        putRow[0].ShouldBe(true);
        long etag = putRow[1].ShouldBeOfType<long>();
        ProtocolResultCompleteMessage.Decode(putComplete.Payload.Span).AffectedCount.ShouldBe(1);

        // Act: GET — [key, value, etag].
        await client.SendAsync(ProtocolMessageType.Execute, Command("GET @k", ("k", Bytes("user:1"))).Encode());

        var headerFrame = await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var header = ProtocolResultHeaderMessage.Decode(headerFrame.Payload.Span);
        header.Columns.Count.ShouldBe(3);
        header.Columns[0].ShouldBe(("key", (byte)DatabaseType.Binary));
        header.Columns[1].ShouldBe(("value", (byte)DatabaseType.Binary));
        header.Columns[2].ShouldBe(("etag", (byte)DatabaseType.Int64));

        var getRow = DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray());
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);

        getRow[0].ShouldBe(Bytes("user:1"));
        getRow[1].ShouldBe(Bytes("ada"));
        getRow[2].ShouldBe(etag);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server execute: DELETE returns its affected count and SCAN streams in key order")]
    public async Task Execute_DeleteAndScan_ShouldReportOutcomes()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        foreach (string key in new[] { "b", "a", "c" })
        {
            await client.SendAsync(ProtocolMessageType.Execute, Command("PUT @k @v", ("k", Bytes(key)), ("v", Bytes("v-" + key))).Encode());
            await client.ExpectAsync(ProtocolMessageType.ResultHeader);
            await client.ExpectAsync(ProtocolMessageType.ResultRow);
            await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        }

        // Act: DELETE rides the plain-result path (no header — just complete).
        await client.SendAsync(ProtocolMessageType.Execute, Command("DELETE @k", ("k", Bytes("b"))).Encode());
        var deleteComplete = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(deleteComplete.Payload.Span).AffectedCount.ShouldBe(1);

        // Act: SCAN streams the survivors in ascending key order.
        await client.SendAsync(ProtocolMessageType.Execute, Command("SCAN").Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);

        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray())[0].ShouldBe(Bytes("a"));
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray())[0].ShouldBe(Bytes("c"));
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server execute: grammar violations report ParseFailure and keep the session alive")]
    public async Task Execute_GrammarViolation_ShouldReportParseFailureAndKeepSessionAlive()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        // Act
        await client.SendAsync(ProtocolMessageType.Execute, Command("FETCH @k", ("k", Bytes("k"))).Encode());

        // Assert: parse failure, then the session still executes.
        var errorFrame = await client.ExpectAsync(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(errorFrame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ParseFailure);

        await client.SendAsync(ProtocolMessageType.Execute, Command("EXISTS @k", ("k", Bytes("k"))).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray())[0].ShouldBe(false);
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);
    }

    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Server execute: a conditional miss is a first-class outcome on the wire, not an error")]
    public async Task Execute_ConditionalMiss_ShouldReturnNotAppliedOutcome()
    {
        // Arrange
        await using var harness = await KeyValueServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();

        await client.SendAsync(ProtocolMessageType.Execute, Command("PUT @k @v", ("k", Bytes("k")), ("v", Bytes("v1"))).Encode());
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        long etag = (long)DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray())[1]!;
        await client.ExpectAsync(ProtocolMessageType.ResultComplete);

        // Act: a stale compare-and-swap.
        await client.SendAsync(ProtocolMessageType.Execute,
            Command("PUT @k @v IF @etag", ("k", Bytes("k")), ("v", Bytes("v2")), ("etag", etag + 1000)).Encode());

        // Assert: applied=false with the current etag — an outcome row, no Error frame.
        await client.ExpectAsync(ProtocolMessageType.ResultHeader);
        var row = DecodeRow((await client.ExpectAsync(ProtocolMessageType.ResultRow)).Payload.ToArray());
        row[0].ShouldBe(false);
        row[1].ShouldBe(etag);
        var complete = await client.ExpectAsync(ProtocolMessageType.ResultComplete);
        ProtocolResultCompleteMessage.Decode(complete.Payload.Span).AffectedCount.ShouldBe(0);
    }
}
