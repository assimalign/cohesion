using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Protocol.Tests;

public class ProtocolFrameHeaderTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Protocol: Frame header round-trips through its encoding")]
    public void WriteTo_ThenTryParse_ShouldRoundTrip()
    {
        // Arrange
        var header = new ProtocolFrameHeader(ProtocolMessageType.Execute, 1234);
        Span<byte> buffer = stackalloc byte[ProtocolFrameHeader.Size];

        // Act
        header.WriteTo(buffer);
        var parsed = ProtocolFrameHeader.TryParse(buffer, out var result);

        // Assert
        parsed.ShouldBeTrue();
        result.Type.ShouldBe(ProtocolMessageType.Execute);
        result.PayloadLength.ShouldBe(1234u);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Protocol: Truncated header does not parse")]
    public void TryParse_TruncatedBuffer_ShouldReturnFalse()
    {
        // Arrange
        Span<byte> buffer = stackalloc byte[ProtocolFrameHeader.Size - 1];

        // Act
        var parsed = ProtocolFrameHeader.TryParse(buffer, out _);

        // Assert
        parsed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Protocol: Oversized length prefix is rejected")]
    public void TryParse_LengthAboveMaximum_ShouldReturnFalse()
    {
        // Arrange
        var header = new ProtocolFrameHeader(ProtocolMessageType.Execute, ProtocolFrameHeader.MaxPayloadLength);
        Span<byte> buffer = stackalloc byte[ProtocolFrameHeader.Size];
        header.WriteTo(buffer);
        // Bump the encoded length one past the maximum.
        buffer[3] += 1;

        // Act
        var parsed = ProtocolFrameHeader.TryParse(buffer, out _);

        // Assert
        parsed.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Protocol: Frame exposes a matching header")]
    public void Frame_Header_ShouldMatchPayload()
    {
        // Arrange
        var frame = new ProtocolFrame(ProtocolMessageType.ResultRow, new byte[42]);

        // Assert
        frame.Header.Type.ShouldBe(ProtocolMessageType.ResultRow);
        frame.Header.PayloadLength.ShouldBe(42u);
    }
}
