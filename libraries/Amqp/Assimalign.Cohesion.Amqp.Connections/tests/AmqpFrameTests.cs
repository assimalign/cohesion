using System;
using System.Buffers;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

public class AmqpFrameTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Write: Should write the frame size, data offset, type and channel before the body")]
    public void Write_WithPerformative_ShouldWriteFrameHeader()
    {
        // Arrange
        using AmqpBufferWriter writer = new();

        // Act
        AmqpFrame.Write(writer, AmqpFrame.AmqpType, 7, new AmqpClosePerformative(), [0xff]);

        // Assert
        Convert.ToHexStringLower(writer.WrittenSpan).ShouldBe("0000000d0200000700531845ff");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - WriteEmpty: Should write an eight-byte frame with no body")]
    public void WriteEmpty_OnWriter_ShouldWriteHeartbeatFrame()
    {
        // Arrange
        using AmqpBufferWriter writer = new();

        // Act
        AmqpFrame.WriteEmpty(writer);

        // Assert
        Convert.ToHexStringLower(writer.WrittenSpan).ShouldBe("0000000802000000");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should read one frame and leave the bytes after it")]
    public void TryRead_WithFrameAndTrailingBytes_ShouldReadFrame()
    {
        // Arrange
        ReadOnlySequence<byte> buffer = new(Convert.FromHexString("0000000d0201000300531845" + "ff" + "0000"));

        // Act
        bool read = AmqpFrame.TryRead(ref buffer, 512, out AmqpFrameHeader header, out ReadOnlySequence<byte> body);

        // Assert
        read.ShouldBeTrue();
        header.Size.ShouldBe(13u);
        header.DataOffset.ShouldBe((byte)2);
        header.Type.ShouldBe(AmqpFrame.SaslType);
        header.Channel.ShouldBe((ushort)3);
        Convert.ToHexStringLower(body.ToArray()).ShouldBe("00531845ff");
        Convert.ToHexStringLower(buffer.ToArray()).ShouldBe("0000");
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should skip an extended header the data offset covers")]
    public void TryRead_WithExtendedHeader_ShouldStartBodyAtDataOffset()
    {
        // Arrange
        ReadOnlySequence<byte> buffer = new(Convert.FromHexString("00000010030000000a0b0c0d00531845"));

        // Act
        bool read = AmqpFrame.TryRead(ref buffer, 512, out AmqpFrameHeader header, out ReadOnlySequence<byte> body);

        // Assert
        read.ShouldBeTrue();
        header.DataOffset.ShouldBe((byte)3);
        Convert.ToHexStringLower(body.ToArray()).ShouldBe("00531845");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should wait for the rest of a partial frame")]
    [InlineData("000000")]
    [InlineData("0000000d02000000005318")]
    public void TryRead_WithPartialFrame_ShouldReturnFalse(string hex)
    {
        // Arrange
        ReadOnlySequence<byte> buffer = new(Convert.FromHexString(hex));
        long length = buffer.Length;

        // Act
        bool read = AmqpFrame.TryRead(ref buffer, 512, out _, out _);

        // Assert
        read.ShouldBeFalse();
        buffer.Length.ShouldBe(length);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should fail a malformed frame header with a framing error")]
    [InlineData("0000000702000000", nameof(ErrorMessages.FrameSizeInvalid))]
    [InlineData("0000000801000000", nameof(ErrorMessages.FrameDataOffsetInvalid))]
    [InlineData("0000000803000000", nameof(ErrorMessages.FrameDataOffsetInvalid))]
    public void TryRead_WithMalformedHeader_ShouldThrowFramingError(string hex, string messageName)
    {
        // Arrange
        byte[] bytes = Convert.FromHexString(hex);

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => TryRead(bytes, 512));

        // Assert
        exception.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
        exception.Message.ShouldBe(ErrorMessages.ResourceManager.GetString(messageName, ErrorMessages.Culture));
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should refuse a frame larger than the negotiated maximum before it arrives")]
    public void TryRead_WithOversizedFrameHeader_ShouldThrowFramingError()
    {
        // Arrange
        byte[] bytes = Convert.FromHexString("0000020102000000");

        // Act
        AmqpException exception = Should.Throw<AmqpException>(() => TryRead(bytes, 512));

        // Assert
        exception.Error.ShouldNotBeNull().Condition.ShouldBe(AmqpErrorCondition.FramingError);
        exception.Message.ShouldContain("513", Case.Sensitive);
        exception.Message.ShouldContain("512", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - TryRead: Should read a frame split across buffer segments")]
    public void TryRead_WithSegmentedBuffer_ShouldReadFrame()
    {
        // Arrange
        byte[] frame = Convert.FromHexString("0000000c0200000000531845");
        ReadOnlySequence<byte> buffer = CreateSegmented(frame, 3);

        // Act
        bool read = AmqpFrame.TryRead(ref buffer, 512, out AmqpFrameHeader header, out ReadOnlySequence<byte> body);

        // Assert
        read.ShouldBeTrue();
        header.Size.ShouldBe(12u);
        Convert.ToHexStringLower(body.ToArray()).ShouldBe("00531845");
        buffer.IsEmpty.ShouldBeTrue();
    }

    private static bool TryRead(byte[] bytes, uint maxFrameSize)
    {
        ReadOnlySequence<byte> buffer = new(bytes);

        return AmqpFrame.TryRead(ref buffer, maxFrameSize, out _, out _);
    }

    private static ReadOnlySequence<byte> CreateSegmented(byte[] bytes, int segmentLength)
    {
        TestSegment first = new(bytes.AsMemory(0, Math.Min(segmentLength, bytes.Length)), 0);
        TestSegment last = first;

        for (int offset = segmentLength; offset < bytes.Length; offset += segmentLength)
        {
            last = last.Append(bytes.AsMemory(offset, Math.Min(segmentLength, bytes.Length - offset)));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            TestSegment next = new(memory, RunningIndex + Memory.Length);

            Next = next;
            return next;
        }
    }
}
