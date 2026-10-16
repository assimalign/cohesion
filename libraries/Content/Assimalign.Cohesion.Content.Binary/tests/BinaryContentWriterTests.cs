using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Binary.Tests;

public class BinaryContentWriterTests
{
    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Byte: agrees with the encoded representation")]
    public void ReadWriteByte_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = data[0];
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 1));

        // Act
        var value = reader.ReadByte();
        writer.WriteByte(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(1);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 1)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(1);
        output.Requests.ShouldBe(new[] { 1 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - SByte: agrees with the encoded representation")]
    public void ReadWriteSByte_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = unchecked((sbyte)data[0]);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 1));

        // Act
        var value = reader.ReadSByte();
        writer.WriteSByte(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(1);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 1)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(1);
        output.Requests.ShouldBe(new[] { 1 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt16BigEndian: agrees with the encoded representation")]
    public void ReadWriteUInt16BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt16BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 2));

        // Act
        var value = reader.ReadUInt16BigEndian();
        writer.WriteUInt16BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(2);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 2)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(2);
        output.Requests.ShouldBe(new[] { 2 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int16BigEndian: agrees with the encoded representation")]
    public void ReadWriteInt16BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt16BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 2));

        // Act
        var value = reader.ReadInt16BigEndian();
        writer.WriteInt16BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(2);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 2)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(2);
        output.Requests.ShouldBe(new[] { 2 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt32BigEndian: agrees with the encoded representation")]
    public void ReadWriteUInt32BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt32BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadUInt32BigEndian();
        writer.WriteUInt32BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int32BigEndian: agrees with the encoded representation")]
    public void ReadWriteInt32BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt32BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadInt32BigEndian();
        writer.WriteInt32BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt64BigEndian: agrees with the encoded representation")]
    public void ReadWriteUInt64BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt64BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 8));

        // Act
        var value = reader.ReadUInt64BigEndian();
        writer.WriteUInt64BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(8);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 8)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(8);
        output.Requests.ShouldBe(new[] { 8 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int64BigEndian: agrees with the encoded representation")]
    public void ReadWriteInt64BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt64BigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 8));

        // Act
        var value = reader.ReadInt64BigEndian();
        writer.WriteInt64BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(8);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 8)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(8);
        output.Requests.ShouldBe(new[] { 8 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt16LittleEndian: agrees with the encoded representation")]
    public void ReadWriteUInt16LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 2));

        // Act
        var value = reader.ReadUInt16LittleEndian();
        writer.WriteUInt16LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(2);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 2)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(2);
        output.Requests.ShouldBe(new[] { 2 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int16LittleEndian: agrees with the encoded representation")]
    public void ReadWriteInt16LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt16LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 2));

        // Act
        var value = reader.ReadInt16LittleEndian();
        writer.WriteInt16LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(2);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 2)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(2);
        output.Requests.ShouldBe(new[] { 2 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt32LittleEndian: agrees with the encoded representation")]
    public void ReadWriteUInt32LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadUInt32LittleEndian();
        writer.WriteUInt32LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int32LittleEndian: agrees with the encoded representation")]
    public void ReadWriteInt32LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt32LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadInt32LittleEndian();
        writer.WriteInt32LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt64LittleEndian: agrees with the encoded representation")]
    public void ReadWriteUInt64LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 8));

        // Act
        var value = reader.ReadUInt64LittleEndian();
        writer.WriteUInt64LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(8);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 8)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(8);
        output.Requests.ShouldBe(new[] { 8 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Int64LittleEndian: agrees with the encoded representation")]
    public void ReadWriteInt64LittleEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadInt64LittleEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 8));

        // Act
        var value = reader.ReadInt64LittleEndian();
        writer.WriteInt64LittleEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(8);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 8)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(8);
        output.Requests.ShouldBe(new[] { 8 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UInt24BigEndian: agrees with the encoded representation")]
    public void ReadWriteUInt24BigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = 0x812345U;
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 3));

        // Act
        var value = reader.ReadUInt24BigEndian();
        writer.WriteUInt24BigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(3);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 3)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(3);
        output.Requests.ShouldBe(new[] { 3 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - SingleBigEndian: agrees with the encoded representation")]
    public void ReadWriteSingleBigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadSingleBigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadSingleBigEndian();
        writer.WriteSingleBigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - DoubleBigEndian: agrees with the encoded representation")]
    public void ReadWriteDoubleBigEndian_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = BinaryPrimitives.ReadDoubleBigEndian(data);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 8));

        // Act
        var value = reader.ReadDoubleBigEndian();
        writer.WriteDoubleBigEndian(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(8);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 8)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(8);
        output.Requests.ShouldBe(new[] { 8 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FourCC: agrees with the encoded representation")]
    public void ReadWriteFourCC_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = FourCC.FromUInt32(0x81234567U);
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadFourCC();
        writer.WriteFourCC(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint16x16: agrees with the encoded representation")]
    public void ReadWriteFixedPoint16x16_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = FixedPoint16x16.FromRaw(BinaryPrimitives.ReadInt32BigEndian(data));
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadFixedPoint16x16();
        writer.WriteFixedPoint16x16(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - UnsignedFixedPoint16x16: agrees with the encoded representation")]
    public void ReadWriteUnsignedFixedPoint16x16_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = UnsignedFixedPoint16x16.FromRaw(BinaryPrimitives.ReadUInt32BigEndian(data));
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadUnsignedFixedPoint16x16();
        writer.WriteUnsignedFixedPoint16x16(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint8x8: agrees with the encoded representation")]
    public void ReadWriteFixedPoint8x8_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = FixedPoint8x8.FromRaw(BinaryPrimitives.ReadInt16BigEndian(data));
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 2));

        // Act
        var value = reader.ReadFixedPoint8x8();
        writer.WriteFixedPoint8x8(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(2);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 2)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(2);
        output.Requests.ShouldBe(new[] { 2 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - FixedPoint2x30: agrees with the encoded representation")]
    public void ReadWriteFixedPoint2x30_KnownBytes_PreservesRepresentation()
    {
        // Arrange: hand-authored field bytes, independent of our writer.
        byte[] data = [0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var expected = FixedPoint2x30.FromRaw(BinaryPrimitives.ReadInt32BigEndian(data));
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);
        var reader = new BinaryContentReader(data.AsSpan(0, 4));

        // Act
        var value = reader.ReadFixedPoint2x30();
        writer.WriteFixedPoint2x30(expected);

        // Assert
        value.ShouldBe(expected);
        reader.Consumed.ShouldBe(4);
        output.WrittenSpan.SequenceEqual(data.AsSpan(0, 4)).ShouldBeTrue();
        writer.BytesWritten.ShouldBe(4);
        output.Requests.ShouldBe(new[] { 4 });
        output.Advances.ShouldBe(output.Requests);
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Variable integers: big and little endian agree with BinaryPrimitives")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void ReadWriteUInt_VariableWidth_MatchesBinaryPrimitives(int width)
    {
        // Arrange
        var mask = width == 8 ? ulong.MaxValue : (1UL << (width * 8)) - 1;
        var value = 0x8123456789ABCDEFUL & mask;
        Span<byte> expected = stackalloc byte[8];

        // Act / Assert
        foreach (var little in new[] { false, true })
        {
            var output = new RecordingBufferWriter();
            var writer = new BinaryContentWriter(output);
            if (little)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(expected, value);
                writer.WriteUIntLittleEndian(value, width);
                output.WrittenSpan.SequenceEqual(expected[..width]).ShouldBeTrue();
            }
            else
            {
                BinaryPrimitives.WriteUInt64BigEndian(expected, value);
                writer.WriteUIntBigEndian(value, width);
                output.WrittenSpan.SequenceEqual(expected[(8 - width)..]).ShouldBeTrue();
            }
            var reader = new BinaryContentReader(output.WrittenSpan);
            (little ? reader.ReadUIntLittleEndian(width) : reader.ReadUIntBigEndian(width)).ShouldBe(value);
            writer.BytesWritten.ShouldBe(width);
            output.Requests.Count.ShouldBe(width == 0 ? 0 : 1);
            output.Advances.ShouldBe(output.Requests);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Writer: strings and zeros each request exactly one span")]
    public void Write_BytesZerosAndStrings_RequestsExactSizes()
    {
        // Arrange
        var output = new RecordingBufferWriter();
        var writer = new BinaryContentWriter(output);

        // Act
        writer.WriteBytes([1, 2, 3]);
        writer.WriteZeros(4);
        writer.WriteUtf8String("hé");
        writer.WriteNullTerminatedUtf8String("©nam");
        writer.WriteNullTerminatedUtf8String("");
        writer.WriteBytes([]);
        writer.WriteZeros(0);
        writer.WriteUtf8String("");

        // Assert
        output.Requests.ShouldBe(new[] { 3, 4, 3, 6, 1 });
        output.Advances.ShouldBe(output.Requests);
        writer.BytesWritten.ShouldBe(17);
        var reader = new BinaryContentReader(output.WrittenSpan);
        reader.ReadBytes(3).ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        reader.ReadBytes(4).ToArray().ShouldBe(new byte[4]);
        reader.ReadUtf8String(3).ShouldBe("hé");
        reader.ReadNullTerminatedUtf8String().ShouldBe("©nam");
        reader.ReadNullTerminatedUtf8String().ShouldBe("");
        reader.IsEnd.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Writer: values that do not fit fail before output is requested")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void WriteUInt_ValueTooLarge_LeavesOutputUntouched(int width)
    {
        // Arrange
        var output = new RecordingBufferWriter();
        var value = 1UL << (width * 8);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BinaryContentWriter(output);
            writer.WriteUIntBigEndian(value, width);
        });
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BinaryContentWriter(output);
            writer.WriteUIntLittleEndian(value, width);
        });
        output.Requests.ShouldBeEmpty();
        output.WrittenSpan.IsEmpty.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Content.Binary] - Writer: invalid widths are argument errors")]
    [InlineData(-1)]
    [InlineData(9)]
    [InlineData(int.MaxValue)]
    public void WriteUInt_InvalidWidth_RejectsBeforeOutput(int width)
    {
        // Arrange
        var output = new RecordingBufferWriter();

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(() => { var writer = new BinaryContentWriter(output); writer.WriteUIntBigEndian(0, width); });
        Should.Throw<ArgumentOutOfRangeException>(() => { var writer = new BinaryContentWriter(output); writer.WriteUIntLittleEndian(0, width); });
        output.Requests.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Content.Binary] - Writer: nulls, negative zero counts and oversized 24-bit values are rejected")]
    public void Write_InvalidArguments_LeavesOutputUntouched()
    {
        // Arrange
        var output = new RecordingBufferWriter();

        // Act / Assert
        Should.Throw<ArgumentNullException>(() => { var writer = new BinaryContentWriter(null!); });
        Should.Throw<ArgumentNullException>(() => { var writer = new BinaryContentWriter(output); writer.WriteUtf8String(null!); });
        Should.Throw<ArgumentNullException>(() => { var writer = new BinaryContentWriter(output); writer.WriteNullTerminatedUtf8String(null!); });
        Should.Throw<ArgumentOutOfRangeException>(() => { var writer = new BinaryContentWriter(output); writer.WriteZeros(-1); });
        Should.Throw<ArgumentOutOfRangeException>(() => { var writer = new BinaryContentWriter(output); writer.WriteUInt24BigEndian(0x1000000); });
        output.Requests.ShouldBeEmpty();
    }
}
