using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public class ProtocolFamilyTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Family: reserved and duplicate identifiers are rejected")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(63)]
    public void Constructor_ReservedIdentifier_ShouldReject(byte identifier)
    {
        Should.Throw<ArgumentException>(() => new ProtocolMessageFamily("test", identifier));
        Should.Throw<ArgumentException>(() => new ProtocolMessageFamily("test", 64, 64));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Family: endpoint binding copies its identifier set")]
    public void Constructor_MutatedInput_ShouldKeepOriginalFamily()
    {
        byte[] identifiers = [5, 64, 255];
        var family = new ProtocolMessageFamily("first", identifiers);
        identifiers[1] = 65;

        family.Supports((ProtocolMessageType)64).ShouldBeTrue();
        family.Supports((ProtocolMessageType)65).ShouldBeFalse();
        family.Supports(ProtocolMessageType.Ping).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Channel: rejects an unrelated family's identifier in both directions")]
    public async Task Channel_UnknownIdentifier_ShouldRejectReadAndWrite()
    {
        using var stream = new MemoryStream();
        var family = new ProtocolMessageFamily("first", 64);
        await using var channel = new ProtocolChannel(stream, family, leaveOpen: true);

        await Should.ThrowAsync<ProtocolException>(async () =>
            await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)65, new byte[] { 1 }), CancellationToken.None));
        stream.Length.ShouldBe(0);

        await using var raw = ProtocolFraming.CreateWriter(stream, leaveOpen: true);
        await raw.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)65, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        stream.Position = 0;
        await Should.ThrowAsync<ProtocolException>(async () => await channel.Reader.ReadFrameAsync(CancellationToken.None));
        channel.Family.ShouldBeSameAs(family);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Protocol] - Channel: shared and model identifiers preserve their wire bytes")]
    public async Task Channel_KnownIdentifiers_ShouldRoundTrip()
    {
        using var stream = new MemoryStream();
        await using var channel = new ProtocolChannel(stream, new ProtocolMessageFamily("model", 5, 64), leaveOpen: true);
        await channel.Writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)5, new byte[] { 42 }), CancellationToken.None);
        await channel.Writer.WriteFrameAsync(new ProtocolFrame(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        await channel.Writer.FlushAsync(CancellationToken.None);
        stream.ToArray().ShouldBe(new byte[] { 0, 0, 0, 1, 5, 42, 0, 0, 0, 0, 11 });
        stream.Position = 0;
        (await channel.Reader.ReadFrameAsync(CancellationToken.None))!.Value.Type.ShouldBe((ProtocolMessageType)5);
        (await channel.Reader.ReadFrameAsync(CancellationToken.None))!.Value.Type.ShouldBe(ProtocolMessageType.Ping);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Protocol] - Version: incompatible majors reject and compatible minors negotiate")]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 99, true)]
    [InlineData(2, 0, false)]
    public void TryNegotiate_PeerVersion_ShouldEnforceMajor(ushort major, ushort minor, bool accepted)
    {
        ProtocolVersion.TryNegotiate(new ProtocolVersion(major, minor), out var negotiated).ShouldBe(accepted);
        negotiated.ShouldBe(accepted ? new ProtocolVersion(1, 0) : default);
    }
}
