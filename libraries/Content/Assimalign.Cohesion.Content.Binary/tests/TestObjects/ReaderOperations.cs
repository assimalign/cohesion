using System;

namespace Assimalign.Cohesion.Content.Binary.Tests;

internal static class ReaderOperations
{
    internal static void Read(ref BinaryContentReader reader, string method, int count)
    {
        switch (method)
        {
            case "Byte": reader.ReadByte(); break;
            case "SByte": reader.ReadSByte(); break;
            case "UInt16BigEndian": reader.ReadUInt16BigEndian(); break;
            case "Int16BigEndian": reader.ReadInt16BigEndian(); break;
            case "UInt32BigEndian": reader.ReadUInt32BigEndian(); break;
            case "Int32BigEndian": reader.ReadInt32BigEndian(); break;
            case "UInt64BigEndian": reader.ReadUInt64BigEndian(); break;
            case "Int64BigEndian": reader.ReadInt64BigEndian(); break;
            case "UInt16LittleEndian": reader.ReadUInt16LittleEndian(); break;
            case "Int16LittleEndian": reader.ReadInt16LittleEndian(); break;
            case "UInt32LittleEndian": reader.ReadUInt32LittleEndian(); break;
            case "Int32LittleEndian": reader.ReadInt32LittleEndian(); break;
            case "UInt64LittleEndian": reader.ReadUInt64LittleEndian(); break;
            case "Int64LittleEndian": reader.ReadInt64LittleEndian(); break;
            case "UInt24BigEndian": reader.ReadUInt24BigEndian(); break;
            case "SingleBigEndian": reader.ReadSingleBigEndian(); break;
            case "DoubleBigEndian": reader.ReadDoubleBigEndian(); break;
            case "FourCC": reader.ReadFourCC(); break;
            case "FixedPoint16x16": reader.ReadFixedPoint16x16(); break;
            case "UnsignedFixedPoint16x16": reader.ReadUnsignedFixedPoint16x16(); break;
            case "FixedPoint8x8": reader.ReadFixedPoint8x8(); break;
            case "FixedPoint2x30": reader.ReadFixedPoint2x30(); break;
            case "UIntBigEndian": reader.ReadUIntBigEndian(count); break;
            case "UIntLittleEndian": reader.ReadUIntLittleEndian(count); break;
            case "Bytes": reader.ReadBytes(count); break;
            case "Exactly":
                Span<byte> destination = stackalloc byte[8];
                reader.ReadExactly(destination[..count]);
                break;
            case "Skip": reader.Skip(count); break;
            case "Utf8String": reader.ReadUtf8String(count); break;
            default: throw new ArgumentException(nameof(method));
        }
    }

    internal static bool TryRead(ref BinaryContentReader reader, string method, int count) => method switch
    {
        "Byte" => reader.TryReadByte(out _),
        "UInt16BigEndian" => reader.TryReadUInt16BigEndian(out _),
        "UInt32BigEndian" => reader.TryReadUInt32BigEndian(out _),
        "UInt64BigEndian" => reader.TryReadUInt64BigEndian(out _),
        "Bytes" => reader.TryReadBytes(count, out _),
        _ => throw new ArgumentException(nameof(method))
    };
}
