using System;
using System.IO;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Sql.Protocol.Tests;

public sealed class SqlCopyProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Protocol] - COPY: requests reject parameters truncation and trailing bytes")]
    public void Decode_Request_ShouldRequireExactEnvelope()
    {
        // Arrange
        var request = new ProtocolCopyToMessage("COPY t TO STDOUT");
        byte[] payload = request.Encode();
        // Act / Assert
        payload.ShouldBe(ProtocolExecuteMessage.Create(request.Statement).Encode());
        ProtocolCopyToMessage.Decode(payload).ShouldBe(request);
        Should.Throw<ProtocolException>(() => ProtocolCopyToMessage.Decode(payload.AsSpan(0, payload.Length - 1))).Message.ShouldStartWith("CK07005:");
        Should.Throw<ProtocolException>(() => ProtocolCopyToMessage.Decode([.. payload, 0]));
        payload[^1] = 1;
        Should.Throw<ProtocolException>(() => ProtocolCopyToMessage.Decode(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Protocol] - COPY: completion is exact and rejects invalid counters or trailing bytes")]
    public void Decode_Completion_ShouldRequireExactPayload()
    {
        // Arrange / Act
        byte[] payload = new ProtocolCopyCompleteMessage(2, 10).Encode();
        // Assert
        payload.ShouldBe(new byte[] { 0,0,0,0,0,0,0,2,0,0,0,0,0,0,0,10 });
        ProtocolCopyCompleteMessage.Decode(payload).ShouldBe(new ProtocolCopyCompleteMessage(2, 10));
        Should.Throw<ProtocolException>(() => ProtocolCopyCompleteMessage.Decode(payload.AsSpan(0, 15))).Message.ShouldStartWith("CK07005:");
        Should.Throw<ProtocolException>(() => ProtocolCopyCompleteMessage.Decode(new byte[17]));
        Should.Throw<ProtocolException>(() => new ProtocolCopyCompleteMessage(-1, 1).Encode());
        Should.Throw<ProtocolException>(() => new ProtocolCopyCompleteMessage(1, 0).Encode());
        payload[7] = 11;
        Should.Throw<ProtocolException>(() => ProtocolCopyCompleteMessage.Decode(payload));
        payload[0] = 255;
        Should.Throw<ProtocolException>(() => ProtocolCopyCompleteMessage.Decode(payload));
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Protocol] - COPY: every new frame requires the independent negotiated capability")]
    [InlineData(80)] [InlineData(81)] [InlineData(82)] [InlineData(83)] [InlineData(84)]
    public async Task WriteFrameAsync_Unnegotiated_ShouldRefuse(int identifier)
    {
        // Arrange
        using var bytes = new MemoryStream();
        await using var channel = new ProtocolChannel(bytes, SqlProtocol.Family, leaveOpen: true);
        channel.Negotiate(ProtocolVersion.Current, [ProtocolCapability.Diagnostics.Name, ProtocolCapability.TransactionStatus.Name]);
        // Act / Assert
        await Should.ThrowAsync<ProtocolException>(async () => await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)identifier, ReadOnlyMemory<byte>.Empty)));
        bytes.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Protocol] - COPY: version 2.0 remains usable and cannot negotiate streaming")]
    public async Task Negotiate_OlderVersion_ShouldPreserveLegacyFrames()
    {
        // Arrange
        using var bytes = new MemoryStream();
        await using var channel = new ProtocolChannel(bytes, SqlProtocol.Family, leaveOpen: true);
        // Act
        var agreement = channel.Negotiate(new ProtocolVersion(2, 0), [SqlProtocol.OutboundStream.Name]);
        await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)5, ProtocolExecuteMessage.Create("SELECT 1").Encode()));
        // Assert
        agreement.Capabilities.ShouldBeEmpty();
        bytes.Length.ShouldBeGreaterThan(0);
        SqlProtocol.OutboundStream.Name.ShouldBe("sql.outbound-stream");
        SqlProtocol.OutboundStream.MessageTypes.ShouldBe(new byte[] {80,81,82,83,84});
    }
}
