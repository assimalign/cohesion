using System;
using System.Buffers.Binary;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv.Internal;

internal sealed class MkvBlockHeader
{
    private MkvBlockHeader(ulong trackNumber, short timestamp, byte flags, bool simple, long[] sizes, long[] offsets)
    {
        TrackNumber = trackNumber; RelativeTimestamp = timestamp; Flags = flags; IsSimple = simple;
        Sizes = sizes; Offsets = offsets;
    }
    internal ulong TrackNumber { get; }
    internal short RelativeTimestamp { get; }
    internal byte Flags { get; }
    internal bool IsSimple { get; }
    internal long[] Sizes { get; }
    internal long[] Offsets { get; }

    internal static MkvBlockHeader Read(Stream input, long payloadLength, long absoluteOffset, bool simpleBlock, int maxFrames)
    {
        long cursor = 0;
        var track = ReadVint(input, ref cursor, absoluteOffset, out _);
        if (track == 0)
        { throw new ContentFormatException(ErrorMessages.BlockTrackInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset); }
        Span<byte> header = stackalloc byte[3];
        for (var i = 0; i < header.Length; i++) { header[i] = ReadOctet(input, ref cursor, absoluteOffset); }
        var timestamp = BinaryPrimitives.ReadInt16BigEndian(header);
        var flags = header[2];
        if ((flags & (simpleBlock ? 0x70 : 0xF1)) != 0)
        { throw new ContentFormatException(ErrorMessages.BlockFlagsInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor - 1); }
        var mode = (MkvLacing)((flags >> 1) & 3);
        var count = mode == MkvLacing.None ? 1 : ReadOctet(input, ref cursor, absoluteOffset) + 1;
        if (count > maxFrames)
        { throw new ContentFormatException(ErrorMessages.LaceLimit, ContentFormatErrorCode.LimitExceeded, absoluteOffset + cursor - 1); }
        if (mode != MkvLacing.None && count == 1)
        { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor - 1); }
        var sizes = new long[count]; // The wire count was checked against the explicit cap above.
        long total = 0;
        for (var i = 0; i < count - 1 && mode != MkvLacing.FixedSize; i++)
        {
            long size;
            if (mode == MkvLacing.Xiph)
            {
                size = 0;
                byte next;
                do
                {
                    next = ReadOctet(input, ref cursor, absoluteOffset);
                    if (size > payloadLength - next)
                    { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor - 1); }
                    size += next;
                } while (next == 255);
            }
            else
            {
                var raw = ReadVint(input, ref cursor, absoluteOffset, out var width);
                var expanded = i == 0 ? (Int128)raw : (Int128)sizes[i - 1] + raw - ((1L << (width * 7 - 1)) - 1);
                if (expanded < 0 || expanded > long.MaxValue)
                { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor); }
                size = (long)expanded;
            }
            if (size < 0 || size > payloadLength - total)
            { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor); }
            sizes[i] = size; total += size;
        }
        var remaining = payloadLength - cursor;
        if (remaining < total || (mode == MkvLacing.FixedSize && remaining % count != 0))
        { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor); }
        if (mode == MkvLacing.FixedSize) { Array.Fill(sizes, remaining / count); }
        else { sizes[^1] = remaining - total; }
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = absoluteOffset + cursor;
            cursor += sizes[i];
        }
        return new MkvBlockHeader(track, timestamp, flags, simpleBlock, sizes, offsets);
    }
    private static byte ReadOctet(Stream source, ref long cursor, long origin)
    {
        Span<byte> one = stackalloc byte[1];
        if (!source.TryReadExactly(one))
        { throw new ContentFormatException(ErrorMessages.BlockTruncated, ContentFormatErrorCode.Truncated, origin + cursor); }
        cursor++;
        return one[0];
    }

    private static ulong ReadVint(Stream source, ref long cursor, long origin, out int width)
    {
        Span<byte> bytes = stackalloc byte[8];
        bytes[0] = ReadOctet(source, ref cursor, origin);
        if (bytes[0] == 0)
        { throw new ContentFormatException(ErrorMessages.BlockInvalid, ContentFormatErrorCode.InvalidValue, origin + cursor - 1); }
        width = 1;
        for (var marker = 0x80; (bytes[0] & marker) == 0; marker >>= 1) { width++; }
        ulong encoded = bytes[0];
        for (var i = 1; i < width; i++) { encoded = (encoded << 8) | ReadOctet(source, ref cursor, origin); }
        // Track and lace VINTs use every data value, including all-ones (not the size sentinel).
        return VInt.FromEncoded(encoded).Value;
    }
}
