using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

/// <summary>
/// Tests for the frame reader/writer implementations and the message payload
/// codecs (#852, protocol slice): round-trips over a stream, clean end-of-stream,
/// truncation and bound violations failing loudly.
/// </summary>
public class ProtocolFramingTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Framing: frames round-trip through a stream in order")]
    public async Task WriteFrames_ThenRead_ShouldRoundTripInOrder()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using (var writer = ProtocolFrameWriter.Create(stream, leaveOpen: true))
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Startup, new byte[] { 1, 2, 3 }));
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty));
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Terminate, new byte[] { 9 }));
            await writer.FlushAsync();
        }

        stream.Position = 0;

        // Act / Assert
        await using var reader = ProtocolFrameReader.Create(stream, leaveOpen: true);

        var first = await reader.ReadFrameAsync();
        first!.Value.Type.ShouldBe(ProtocolMessageType.Startup);
        first.Value.Payload.ToArray().ShouldBe(new byte[] { 1, 2, 3 });

        (await reader.ReadFrameAsync())!.Value.Type.ShouldBe(ProtocolMessageType.Ping);
        (await reader.ReadFrameAsync())!.Value.Type.ShouldBe(ProtocolMessageType.Terminate);
        (await reader.ReadFrameAsync()).ShouldBeNull(); // clean end of stream
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Framing: truncated frames and oversized declarations fail loudly")]
    public async Task ReadFrame_TruncatedOrOversized_ShouldThrow()
    {
        // Truncated payload: header declares 10 bytes, stream carries 2.
        using var truncated = new MemoryStream();
        var header = new byte[ProtocolFrameHeader.Size];
        new ProtocolFrameHeader(ProtocolMessageType.Startup, 10).WriteTo(header);
        truncated.Write(header);
        truncated.Write(new byte[] { 1, 2 });
        truncated.Position = 0;

        await using (var reader = ProtocolFrameReader.Create(truncated, leaveOpen: true))
        {
            await Should.ThrowAsync<ProtocolException>(async () => await reader.ReadFrameAsync());
        }

        // Oversized declaration: length beyond MaxPayloadLength is rejected from
        // the header alone — no allocation happens.
        using var oversized = new MemoryStream();
        var bad = new byte[ProtocolFrameHeader.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bad, ProtocolFrameHeader.MaxPayloadLength + 1);
        bad[4] = (byte)ProtocolMessageType.Startup;
        oversized.Write(bad);
        oversized.Position = 0;

        await using (var reader = ProtocolFrameReader.Create(oversized, leaveOpen: true))
        {
            await Should.ThrowAsync<ProtocolException>(async () => await reader.ReadFrameAsync());
        }
    }

    /// <summary>
    /// The payload bound lives in the base's public <c>WriteFrameAsync</c> (owner decision 29 of
    /// 2026-10-06): the stream writer, the channel's family writer and a leaf of another assembly
    /// (this test's) all refuse an oversized payload with the same synchronous error before their
    /// core runs, and a payload of exactly the bound reaches the core.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Framing: every writer refuses an oversized payload the same way before its core runs")]
    public async Task WriteFrame_OversizedPayload_EveryWriterShouldRefuseBeforeItsCore()
    {
        // Arrange
        var payload = new byte[ProtocolFrameHeader.MaxPayloadLength + 1];
        var oversized = new ProtocolFrame((ProtocolMessageType)64, payload);
        using var stream = new MemoryStream();
        await using var raw = ProtocolFrameWriter.Create(stream, leaveOpen: true);
        await using var channel = new ProtocolChannel(stream, new ProtocolMessageFamily("model", 64), leaveOpen: true);
        var recording = new RecordingFrameWriter();
        var writers = new[] { raw, channel.Writer, recording };

        // Act: each refusal is thrown by the call itself, not through the returned task.
        var messages = new List<string>();
        foreach (var writer in writers)
        {
            messages.Add(Should.Throw<ProtocolException>(() => { _ = writer.WriteFrameAsync(oversized); }).Message);
        }

        await recording.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)64, payload.AsMemory(0, (int)ProtocolFrameHeader.MaxPayloadLength)));

        // Assert
        messages.ShouldAllBe(message => message == messages[0]);
        messages[0].ShouldContain($"exceeds the {ProtocolFrameHeader.MaxPayloadLength}-byte maximum");
        stream.Length.ShouldBe(0);
        recording.WrittenLengths.ShouldBe(new[] { (int)ProtocolFrameHeader.MaxPayloadLength });
    }

    /// <summary>
    /// The bound runs before any core, so a frame that fails both it and the channel's family
    /// check reports the bound; a frame failing only the family check still reports the family.
    /// Before decision 29 the bound sat in the stream writer's core, behind the family check, and
    /// the same frame reported the family.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Channel: a frame failing both the payload bound and the family check reports the bound")]
    public async Task Channel_OversizedPayloadOutsideTheFamily_ShouldReportTheBoundFirst()
    {
        // Arrange
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, new ProtocolMessageFamily("model", 64), leaveOpen: true);
        var payload = new byte[ProtocolFrameHeader.MaxPayloadLength + 1];

        // Act
        var both = Should.Throw<ProtocolException>(
            () => { _ = channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)65, payload)); });
        var familyOnly = Should.Throw<ProtocolException>(
            () => { _ = channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)65, payload.AsMemory(0, 1))); });

        // Assert
        both.Message.ShouldContain("exceeds the");
        both.Message.ShouldNotContain("endpoint family");
        familyOnly.Message.ShouldContain("endpoint family 'model'");
        stream.Length.ShouldBe(0);
    }

    /// <summary>A leaf outside Database.Protocol: it records what reaches its core.</summary>
    private sealed class RecordingFrameWriter : ProtocolFrameWriter
    {
        public List<int> WrittenLengths { get; } = new();

        protected override ValueTask WriteFrameCoreAsync(ProtocolFrame frame, System.Threading.CancellationToken cancellationToken)
        {
            WrittenLengths.Add(frame.Payload.Length);
            return default;
        }

        protected override ValueTask FlushCoreAsync(System.Threading.CancellationToken cancellationToken) => default;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Messages: startup and error payloads round-trip")]
    public void Messages_EncodeDecode_ShouldRoundTrip()
    {
        var startup = new ProtocolStartupMessage(ProtocolVersion.Current, "appdb", "svc-user");
        var decodedStartup = ProtocolStartupMessage.Decode(startup.Encode());
        decodedStartup.ShouldBe(startup);

        var error = new ProtocolErrorMessage(ProtocolErrorCode.AuthenticationFailed, "bad credentials");
        ProtocolErrorMessage.Decode(error.Encode()).ShouldBe(error);


    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Messages: malformed payloads throw ProtocolException")]
    public void Messages_MalformedPayloads_ShouldThrow()
    {
        Should.Throw<ProtocolException>(() => ProtocolStartupMessage.Decode(new byte[] { 0, 1 }));
        Should.Throw<ProtocolException>(() => ProtocolErrorMessage.Decode(new byte[] { 0 }));
    }
}
