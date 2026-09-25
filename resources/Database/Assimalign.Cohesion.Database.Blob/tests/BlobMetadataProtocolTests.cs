using System;
using System.Buffers.Binary;

using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobMetadataProtocolTests
{
    private const string PropertiesVector =
        "0000000162" + // text name = b
        "0000000000000009" + // length = 9
        "00" + // null content type
        "FEDCBA9876543210" + // opaque unsigned entity tag
        "0000000000000000" + // created UTC ticks
        "0000000000000001" + // modified UTC ticks
        "DEADBEEF"; // CRC-32

    [Fact(DisplayName = "Cohesion Test [Blob] - Metadata wire: Independent request and completion vectors")]
    public void Requests_IndependentVectors_ShouldPreserveWireContract()
    {
        byte[] identity = Convert.FromHexString("000000016300000002CEBB");
        new BlobDeleteMessage("c", "λ").Encode().ShouldBe(identity);
        BlobDeleteMessage.Decode(identity).ShouldBe(new("c", "λ"));
        new BlobGetPropertiesMessage("c", "λ").Encode().ShouldBe(identity);
        BlobGetPropertiesMessage.Decode(identity).ShouldBe(new("c", "λ"));
        new BlobListMessage("c", "λ").Encode().ShouldBe(identity);
        BlobListMessage.Decode(identity).ShouldBe(new("c", "λ"));

        byte[] all = Convert.FromHexString("000000016300000000");
        new BlobListMessage("c").Encode().ShouldBe(all);
        BlobListMessage.Decode(all).ShouldBe(new("c", ""));
        byte[] complete = Convert.FromHexString("0000000100000001");
        new BlobOperationCompleteMessage(4_294_967_297).Encode().ShouldBe(complete);
        BlobOperationCompleteMessage.Decode(complete).Count.ShouldBe(4_294_967_297);

        foreach (byte type in new byte[] { 70, 71, 72, 73, 74 })
        {
            BlobProtocol.Family.Supports((ProtocolMessageType)type).ShouldBeTrue();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Metadata wire: Properties preserve every field and unsigned ranges")]
    public void Properties_IndependentVector_ShouldPreserveEveryField()
    {
        var expected = new BlobProperties("b", 9, null, 0xFEDCBA9876543210,
            DateTimeOffset.MinValue, DateTimeOffset.MinValue.AddTicks(1), 0xDEADBEEF);
        byte[] payload = Convert.FromHexString(PropertiesVector);
        new BlobPropertiesMessage(expected).Encode().ShouldBe(payload);
        BlobPropertiesMessage.Decode(payload).Properties.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Metadata wire: Null and empty content types remain distinct")]
    public void Properties_NullAndEmptyContentType_ShouldRemainDistinct()
    {
        byte[] empty = Convert.FromHexString(PropertiesVector[..26] + "0100000000" + PropertiesVector[28..]);
        var properties = BlobPropertiesMessage.Decode(empty).Properties;
        properties.ContentType.ShouldBe("");
        new BlobPropertiesMessage(properties).Encode().ShouldBe(empty);
        BlobPropertiesMessage.Decode(Convert.FromHexString(PropertiesVector)).Properties.ContentType.ShouldBeNull();

        var offset = properties with
        {
            ContentType = "application/test",
            CreatedAt = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(2)),
            ModifiedAt = new DateTimeOffset(2026, 9, 18, 13, 0, 0, TimeSpan.FromHours(2))
        };
        var decoded = BlobPropertiesMessage.Decode(new BlobPropertiesMessage(offset).Encode()).Properties;
        decoded.CreatedAt.UtcTicks.ShouldBe(offset.CreatedAt.UtcTicks);
        decoded.ModifiedAt.UtcTicks.ShouldBe(offset.ModifiedAt.UtcTicks);
        decoded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        decoded.ModifiedAt.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Metadata wire: All truncated property payloads and trailing bytes reject")]
    public void Properties_TruncatedOrTrailingPayload_ShouldReject()
    {
        byte[] payload = Convert.FromHexString(PropertiesVector);
        for (int size = 0; size < payload.Length; size++)
        {
            byte[] truncated = payload[..size];
            Should.Throw<ProtocolException>(() => BlobPropertiesMessage.Decode(truncated));
        }
        byte[] trailing = [.. payload, 0];
        Should.Throw<ProtocolException>(() => BlobPropertiesMessage.Decode(trailing));
    }

    [Theory(DisplayName = "Cohesion Test [Blob] - Metadata wire: Invalid lengths, flags, and timestamps reject")]
    [InlineData(5, -1L)]
    [InlineData(22, -1L)]
    [InlineData(22, long.MaxValue)]
    [InlineData(30, -1L)]
    [InlineData(30, long.MaxValue)]
    public void Properties_InvalidNumericField_ShouldReject(int offset, long value)
    {
        byte[] payload = Convert.FromHexString(PropertiesVector);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset), value);
        Should.Throw<ProtocolException>(() => BlobPropertiesMessage.Decode(payload));
    }

    [Theory(DisplayName = "Cohesion Test [Blob] - Metadata wire: Malformed request strings reject")]
    [InlineData("000000016300000002C080")]
    [InlineData("0000000163FFFFFFFF")]
    [InlineData("000000016300010000")]
    [InlineData("000000016300000001")]
    [InlineData("0000000163000000016200")]
    [InlineData("000000000000000162")]
    public void Requests_MalformedStrings_ShouldReject(string hex)
    {
        byte[] payload = Convert.FromHexString(hex);
        Should.Throw<ProtocolException>(() => BlobDeleteMessage.Decode(payload));
        Should.Throw<ProtocolException>(() => BlobGetPropertiesMessage.Decode(payload));
        Should.Throw<ProtocolException>(() => BlobListMessage.Decode(payload));
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Metadata wire: Invalid properties and completion values reject")]
    public void Metadata_InvalidValues_ShouldReject()
    {
        byte[] flag = Convert.FromHexString(PropertiesVector);
        flag[13] = 2;
        Should.Throw<ProtocolException>(() => BlobPropertiesMessage.Decode(flag));
        Should.Throw<ProtocolException>(() => new BlobOperationCompleteMessage(-1).Encode());
        Should.Throw<ProtocolException>(() => BlobOperationCompleteMessage.Decode(Convert.FromHexString("FFFFFFFFFFFFFFFF")));
        Should.Throw<ProtocolException>(() => BlobOperationCompleteMessage.Decode(new byte[9]));
        var properties = BlobPropertiesMessage.Decode(Convert.FromHexString(PropertiesVector)).Properties;
        Should.Throw<ProtocolException>(() => new BlobPropertiesMessage(properties with { Length = -1 }).Encode());
        Should.Throw<ProtocolException>(() => new BlobPropertiesMessage(properties with { Name = "" }).Encode());
        Should.Throw<ProtocolException>(() => new BlobListMessage("c", new string('x', 65_536)).Encode());
    }
}
