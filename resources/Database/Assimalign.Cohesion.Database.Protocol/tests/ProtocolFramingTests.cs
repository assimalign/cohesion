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
        await using (var writer = ProtocolFraming.CreateWriter(stream, leaveOpen: true))
        {
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Startup, new byte[] { 1, 2, 3 }));
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty));
            await writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Terminate, new byte[] { 9 }));
            await writer.FlushAsync();
        }

        stream.Position = 0;

        // Act / Assert
        await using var reader = ProtocolFraming.CreateReader(stream, leaveOpen: true);

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
        new ProtocolFrameHeader(ProtocolMessageType.Execute, 10).WriteTo(header);
        truncated.Write(header);
        truncated.Write(new byte[] { 1, 2 });
        truncated.Position = 0;

        await using (var reader = ProtocolFraming.CreateReader(truncated, leaveOpen: true))
        {
            await Should.ThrowAsync<ProtocolException>(async () => await reader.ReadFrameAsync());
        }

        // Oversized declaration: length beyond MaxPayloadLength is rejected from
        // the header alone — no allocation happens.
        using var oversized = new MemoryStream();
        var bad = new byte[ProtocolFrameHeader.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bad, ProtocolFrameHeader.MaxPayloadLength + 1);
        bad[4] = (byte)ProtocolMessageType.Execute;
        oversized.Write(bad);
        oversized.Position = 0;

        await using (var reader = ProtocolFraming.CreateReader(oversized, leaveOpen: true))
        {
            await Should.ThrowAsync<ProtocolException>(async () => await reader.ReadFrameAsync());
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Messages: startup, error, execute, and result payloads round-trip")]
    public void Messages_EncodeDecode_ShouldRoundTrip()
    {
        var startup = new ProtocolStartupMessage(ProtocolVersion.Current, "appdb", "svc-user");
        var decodedStartup = ProtocolStartupMessage.Decode(startup.Encode());
        decodedStartup.ShouldBe(startup);

        var error = new ProtocolErrorMessage(ProtocolErrorCode.AuthenticationFailed, "bad credentials");
        ProtocolErrorMessage.Decode(error.Encode()).ShouldBe(error);

        var execute = new ProtocolExecuteMessage("SELECT * FROM users WHERE id = @id;", new Dictionary<string, byte[]>
        {
            ["id"] = new byte[] { 0x05, 0x01, 0x02 },
        });
        var decodedExecute = ProtocolExecuteMessage.Decode(execute.Encode());
        decodedExecute.Statement.ShouldBe(execute.Statement);
        decodedExecute.Parameters["id"].ShouldBe(new byte[] { 0x05, 0x01, 0x02 });

        var headerMessage = new ProtocolResultHeaderMessage(new List<(string, byte)> { ("id", 5), ("name", 9) });
        var decodedHeader = ProtocolResultHeaderMessage.Decode(headerMessage.Encode());
        decodedHeader.Columns.Count.ShouldBe(2);
        decodedHeader.Columns[1].Name.ShouldBe("name");
        decodedHeader.Columns[1].Type.ShouldBe((byte)9);

        var complete = new ProtocolResultCompleteMessage(42);
        ProtocolResultCompleteMessage.Decode(complete.Encode()).AffectedCount.ShouldBe(42);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Messages: malformed payloads throw ProtocolException")]
    public void Messages_MalformedPayloads_ShouldThrow()
    {
        Should.Throw<ProtocolException>(() => ProtocolStartupMessage.Decode(new byte[] { 0, 1 }));
        Should.Throw<ProtocolException>(() => ProtocolErrorMessage.Decode(new byte[] { 0 }));
        Should.Throw<ProtocolException>(() => ProtocolExecuteMessage.Decode(new byte[] { 0, 0, 0, 5, 65 })); // string length beyond payload
        Should.Throw<ProtocolException>(() => ProtocolResultCompleteMessage.Decode(new byte[] { 1, 2 }));
    }
}
