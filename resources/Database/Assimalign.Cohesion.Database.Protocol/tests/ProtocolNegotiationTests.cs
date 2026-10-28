using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public sealed class ProtocolNegotiationTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - 2.0 preserves baseline Ready and Error bytes")]
    public async Task Negotiate_Baseline_ShouldPreserveWireBytes()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, new ProtocolMessageFamily("test", [ProtocolCapability.Diagnostics], null, 64), true);
        // Act
        var ready = channel.Negotiate(new(2, 0), ["diagnostics"]);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Ready, ready.Encode()), CancellationToken.None);
        await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Error, new ProtocolErrorMessage(ProtocolErrorCode.ParseFailure, "x").Encode()), CancellationToken.None);
        // Assert
        ready.Capabilities.ShouldBeEmpty();
        stream.ToArray().ShouldBe(new byte[] {0,0,0,0,4, 0,0,0,7,10, 0,5,0,0,0,1,120});
        ProtocolReadyMessage.Decode([]).Version.ShouldBe(new(2, 0));
        await Should.ThrowAsync<ProtocolException>(async () => await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Diagnostics, ReadOnlyMemory<byte>.Empty), CancellationToken.None));
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - capability intersection gates a second endpoint frame")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Negotiate_SecondCapability_ShouldRequireBothPeers(bool advertised)
    {
        // Arrange
        var extra = new ProtocolCapability("test.extra", 64);
        var family = new ProtocolMessageFamily("test", [ProtocolCapability.Diagnostics, extra], null, 64);
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, family, true);
        // Act
        var ready = channel.Negotiate(ProtocolVersion.Current, advertised ? ["diagnostics", "test.extra", "unknown"] : ["diagnostics"]);
        // Assert
        ready.Capabilities.Contains("test.extra").ShouldBe(advertised);
        if (advertised)
        {
            await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)64, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
            stream.Position = 0;
            (await channel.Reader.ReadFrameAsync(CancellationToken.None))!.Value.Type.ShouldBe((ProtocolMessageType)64);
        }
        else
        {
            await Should.ThrowAsync<ProtocolException>(async () => await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)64, ReadOnlyMemory<byte>.Empty), CancellationToken.None));
            stream.Length.ShouldBe(0);
            await using var raw = ProtocolFrameWriter.Create(stream, true);
            await raw.WriteFrameAsync(new((ProtocolMessageType)64, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
            stream.Position = 0;
            await Should.ThrowAsync<ProtocolException>(async () => await channel.Reader.ReadFrameAsync(CancellationToken.None));
        }
        Should.Throw<ProtocolException>(() => channel.SetAgreement(ready));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Diagnostics requires negotiation in both directions")]
    public async Task Channel_DiagnosticsWithoutAgreement_ShouldRefuseBothDirections()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, new ProtocolMessageFamily("test", [ProtocolCapability.Diagnostics], null, 64), true);
        // Act / Assert
        await Should.ThrowAsync<ProtocolException>(async () => await channel.Writer.WriteFrameAsync(new(ProtocolMessageType.Diagnostics, ReadOnlyMemory<byte>.Empty), CancellationToken.None));
        await using var raw = ProtocolFrameWriter.Create(stream, true);
        await raw.WriteFrameAsync(new(ProtocolMessageType.Diagnostics, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        stream.Position = 0;
        await Should.ThrowAsync<ProtocolException>(async () => await channel.Reader.ReadFrameAsync(CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Startup extension preserves the old four-field prefix")]
    public void Encode_StartupCapabilities_ShouldAppendToBaseline()
    {
        // Arrange
        var startup = new ProtocolStartupMessage(new(2, 1), "db", "user") { Capabilities = ["diagnostics", "test.extra"] };
        // Act
        byte[] payload = startup.Encode();
        int position = 0;
        // Assert: this is the 2.0 decoder, which stopped after principal.
        ProtocolPayload.ReadUInt16(payload, ref position).ShouldBe((ushort)2);
        ProtocolPayload.ReadUInt16(payload, ref position).ShouldBe((ushort)1);
        ProtocolPayload.ReadString(payload, ref position).ShouldBe("db");
        ProtocolPayload.ReadString(payload, ref position).ShouldBe("user");
        position.ShouldBeLessThan(payload.Length);
        ProtocolStartupMessage.Decode(payload).Capabilities.ShouldBe(startup.Capabilities);
        ProtocolStartupMessage.Decode(payload.AsSpan(0, position)).Capabilities.ShouldBeEmpty();
        ProtocolReadyMessage.Decode(new ProtocolReadyMessage(ProtocolVersion.Current, startup.Capabilities).Encode()).Capabilities.ShouldBe(startup.Capabilities);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - malformed capability lists and Ready payloads fail")]
    public void Decode_InvalidCapabilities_ShouldReject()
    {
        // Arrange / Act / Assert
        Should.Throw<ProtocolException>(() => ProtocolReadyMessage.Decode(new byte[] {0}));
        Should.Throw<ProtocolException>(() => ProtocolReadyMessage.Decode(new byte[] {0,2,0,1,0,65}));
        Should.Throw<ProtocolException>(() => new ProtocolReadyMessage(ProtocolVersion.Current, ["diagnostics", "diagnostics"]).Encode());
        Should.Throw<ProtocolException>(() => new ProtocolReadyMessage(ProtocolVersion.Current, ["Bad Name"]).Encode());
        Should.Throw<ProtocolException>(() => new ProtocolReadyMessage(ProtocolVersion.Current, [new string('a',129)]).Encode());
        Should.Throw<ProtocolException>(() => ProtocolReadyMessage.Decode(new ProtocolReadyMessage(ProtocolVersion.Current, []).Encode().Concat(new byte[]{0}).ToArray()));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - null collections fail at construction and init")]
    public void Construct_NullCollections_ShouldRejectEveryAssignmentPath()
    {
        // Arrange
        var ready = new ProtocolReadyMessage(ProtocolVersion.Current, []);
        var diagnostics = new ProtocolDiagnosticsMessage([]);
        // Act / Assert
        Should.Throw<ArgumentNullException>(() => new ProtocolReadyMessage(ProtocolVersion.Current, null!));
        Should.Throw<ArgumentNullException>(() => new ProtocolDiagnosticsMessage(null!));
        Should.Throw<ArgumentNullException>(() => new ProtocolReadyMessage(ProtocolVersion.Current, []) { Capabilities = null! });
        Should.Throw<ArgumentNullException>(() => new ProtocolDiagnosticsMessage([]) { Entries = null! });
        Should.Throw<ArgumentNullException>(() => ready with { Capabilities = null! });
        Should.Throw<ArgumentNullException>(() => diagnostics with { Entries = null! });
        ready.Capabilities.ShouldBeEmpty();
        diagnostics.Entries.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - absent diagnostic strings round trip as null")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Decode_EmptyDiagnosticStrings_ShouldNormalizeToNull(string? code, string? message)
    {
        // Arrange
        var diagnostic = new ProtocolDiagnostic(ProtocolDiagnosticSeverity.Warning, code, message, null, null, 1);
        // Act
        var decoded = ProtocolDiagnosticsMessage.Decode(new ProtocolDiagnosticsMessage([diagnostic]).Encode()).Entries.ShouldHaveSingleItem();
        // Assert
        decoded.Code.ShouldBeNull();
        decoded.Message.ShouldBeNull();
        decoded.ShouldBe(diagnostic with { Code = null, Message = null });
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - hostile Ready names are rejected before string allocation")]
    public void Decode_HostileReadyName_ShouldAllocateOnlyBoundedRejection()
    {
        // Arrange: one 16,000-byte ASCII name fits the Ready payload limit.
        var buffer = new List<byte>();
        ProtocolPayload.WriteUInt16(buffer, 2);
        ProtocolPayload.WriteUInt16(buffer, 1);
        ProtocolPayload.WriteUInt16(buffer, 1);
        ProtocolPayload.WriteString(buffer, new string('a', 16000));
        byte[] payload = buffer.ToArray();
        for (int index = 0; index < 20; index++) { RejectHostileReady(payload); }
        // Act: keep assertion-framework allocations outside the measured synchronous call.
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool rejected = RejectHostileReady(payload);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert: an allocated UTF-16 name alone would exceed 32,000 bytes.
        rejected.ShouldBeTrue();
        allocated.ShouldBeLessThan(4096);
    }

    private static bool RejectHostileReady(byte[] payload)
    {
        try { ProtocolReadyMessage.Decode(payload); return false; }
        catch (ProtocolException) { return true; }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Diagnostics preserves every field and enforces bounds")]
    public void Decode_DiagnosticPayload_ShouldRoundTripAndRejectMalformed()
    {
        // Arrange
        ProtocolDiagnostic[] entries = [new(ProtocolDiagnosticSeverity.Warning,"W1","warning π",0,4,2), new(ProtocolDiagnosticSeverity.Error,"E1","failure",null,null,1)];
        byte[] payload = new ProtocolDiagnosticsMessage(entries).Encode();
        // Act / Assert
        ProtocolDiagnosticsMessage.Decode(payload).Entries.ShouldBe(entries);
        for (int length = 0; length < payload.Length; length++)
        {
            int truncated = length;
            Should.Throw<ProtocolException>(() => ProtocolDiagnosticsMessage.Decode(payload.AsSpan(0,truncated)));
        }
        Should.Throw<ProtocolException>(() => ProtocolDiagnosticsMessage.Decode(payload.Concat(new byte[]{0}).ToArray()));
        payload[4] = 255;
        Should.Throw<ProtocolException>(() => ProtocolDiagnosticsMessage.Decode(payload));
        Should.Throw<ProtocolException>(() => new ProtocolDiagnosticsMessage(Enumerable.Repeat(entries[0],ProtocolDiagnosticsMessage.MaxEntryCount+1).ToArray()).Encode());
        Should.Throw<ProtocolException>(() => new ProtocolDiagnosticsMessage([entries[0] with { Message = new string('x',ProtocolDiagnosticsMessage.MaxPayloadLength) }]).Encode());
        Should.Throw<ProtocolException>(() => ProtocolDiagnosticsMessage.Decode(new byte[ProtocolDiagnosticsMessage.MaxPayloadLength+1]));
        Should.Throw<ProtocolException>(() => new ProtocolDiagnosticsMessage([entries[0] with { End = -1 }]).Encode());
        Should.Throw<ProtocolException>(() => new ProtocolDiagnosticsMessage([entries[0] with { Line = 0 }]).Encode());
    }
}
