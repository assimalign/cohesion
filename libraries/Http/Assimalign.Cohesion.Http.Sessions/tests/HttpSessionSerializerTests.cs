using System.Collections.Generic;
using System.Text;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpSessionSerializerTests
{
    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: Should round-trip a populated dictionary byte-for-byte")]
    public void Serialize_Deserialize_ShouldRoundTripEntries()
    {
        // Arrange
        Dictionary<string, byte[]> values = new()
        {
            ["name"] = Encoding.UTF8.GetBytes("cohesion"),
            ["count"] = [0, 0, 0, 42],
            ["empty"] = [],
        };

        // Act
        byte[] frame = HttpSessionSerializer.Serialize(values);
        bool decoded = HttpSessionSerializer.TryDeserialize(frame, out Dictionary<string, byte[]>? result);

        // Assert
        decoded.ShouldBeTrue();
        result!.Count.ShouldBe(3);
        result["name"].ShouldBe(Encoding.UTF8.GetBytes("cohesion"));
        result["count"].ShouldBe(new byte[] { 0, 0, 0, 42 });
        result["empty"].ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: Should round-trip an empty session")]
    public void Serialize_EmptySession_ShouldRoundTripToEmpty()
    {
        // Arrange
        Dictionary<string, byte[]> values = new();

        // Act
        byte[] frame = HttpSessionSerializer.Serialize(values);
        bool decoded = HttpSessionSerializer.TryDeserialize(frame, out Dictionary<string, byte[]>? result);

        // Assert
        frame.Length.ShouldBe(5); // version + int32 count
        decoded.ShouldBeTrue();
        result!.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: An unrecognized version should be rejected")]
    public void TryDeserialize_UnknownVersion_ShouldReturnFalse()
    {
        // Arrange
        Dictionary<string, byte[]> values = new() { ["k"] = [1] };
        byte[] frame = HttpSessionSerializer.Serialize(values);
        frame[0] = 0xFF; // corrupt the version byte

        // Act
        bool decoded = HttpSessionSerializer.TryDeserialize(frame, out Dictionary<string, byte[]>? result);

        // Assert
        decoded.ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: A truncated frame should be rejected")]
    public void TryDeserialize_TruncatedFrame_ShouldReturnFalse()
    {
        // Arrange
        Dictionary<string, byte[]> values = new() { ["name"] = Encoding.UTF8.GetBytes("cohesion") };
        byte[] frame = HttpSessionSerializer.Serialize(values);
        byte[] truncated = frame[..(frame.Length - 3)];

        // Act
        bool decoded = HttpSessionSerializer.TryDeserialize(truncated, out Dictionary<string, byte[]>? result);

        // Assert
        decoded.ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: Trailing bytes should be rejected")]
    public void TryDeserialize_TrailingBytes_ShouldReturnFalse()
    {
        // Arrange
        Dictionary<string, byte[]> values = new() { ["name"] = [1, 2, 3] };
        byte[] frame = HttpSessionSerializer.Serialize(values);
        byte[] padded = [.. frame, 0x00];

        // Act
        bool decoded = HttpSessionSerializer.TryDeserialize(padded, out Dictionary<string, byte[]>? result);

        // Assert
        decoded.ShouldBeFalse();
        result.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Sessions] - Serializer: An empty buffer should be rejected")]
    public void TryDeserialize_EmptyBuffer_ShouldReturnFalse()
    {
        // Act
        bool decoded = HttpSessionSerializer.TryDeserialize([], out Dictionary<string, byte[]>? result);

        // Assert
        decoded.ShouldBeFalse();
        result.ShouldBeNull();
    }
}
