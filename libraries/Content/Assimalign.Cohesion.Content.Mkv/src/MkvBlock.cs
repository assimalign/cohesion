using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>Describes a Block or SimpleBlock header and its borrowed, unmaterialized frames.</summary>
public sealed class MkvBlock
{
    private MkvBlock(ulong trackNumber, short timestamp, byte flags, bool simple, BinarySegment[] frames, long[] offsets)
    {
        TrackNumber = trackNumber; RelativeTimestamp = timestamp;
        IsKeyframe = simple && (flags & 0x80) != 0; IsInvisible = (flags & 8) != 0;
        IsDiscardable = simple && (flags & 1) != 0; Lacing = (MkvLacing)((flags >> 1) & 3);
        Frames = Array.AsReadOnly(frames); FrameOffsets = Array.AsReadOnly(offsets);
    }

    /// <summary>Gets the nonzero TrackNumber (not TrackUID).</summary>
    public ulong TrackNumber { get; }
    /// <summary>Gets the signed timestamp relative to the enclosing Cluster, in segment ticks.</summary>
    public short RelativeTimestamp { get; }
    /// <summary>Gets the SimpleBlock keyframe flag; BlockGroup sync status comes from ReferenceBlock absence.</summary>
    public bool IsKeyframe { get; }
    /// <summary>Gets whether this frame is invisible.</summary>
    public bool IsInvisible { get; }
    /// <summary>Gets the SimpleBlock discardable flag.</summary>
    public bool IsDiscardable { get; }
    /// <summary>Gets the lacing mode.</summary>
    public MkvLacing Lacing { get; }
    /// <summary>Gets ordered frame segments borrowing the input's lifetime.</summary>
    public IReadOnlyList<BinarySegment> Frames { get; }
    /// <summary>Gets absolute frame offsets in the coordinate system supplied to Parse.</summary>
    public IReadOnlyList<long> FrameOffsets { get; }

    /// <summary>Parses framing only; never reads or copies encoded frame bytes.</summary>
    /// <param name="payload">The complete block payload, excluding its EBML header.</param>
    /// <param name="absoluteOffset">The absolute start of the payload.</param>
    /// <param name="simpleBlock">True for SimpleBlock, false for Block.</param>
    /// <param name="maxFrames">The permitted frame count, from one to 256.</param>
    /// <returns>The validated frame boundaries.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A caller limit or offset is invalid.</exception>
    /// <exception cref="ContentFormatException">The framing is truncated, invalid, or exceeds the frame cap.</exception>
    public static MkvBlock Parse(BinarySegment payload, long absoluteOffset, bool simpleBlock = true, int maxFrames = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrames, 256);
        if (payload.Length > long.MaxValue - absoluteOffset)
        { throw new ContentFormatException(ErrorMessages.BlockInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset); }
        using var input = payload.OpenRead();
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
                    if (size > payload.Length - next)
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
            if (size < 0 || size > payload.Length - total)
            { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor); }
            sizes[i] = size; total += size;
        }
        var remaining = payload.Length - cursor;
        if (remaining < total || (mode == MkvLacing.FixedSize && remaining % count != 0))
        { throw new ContentFormatException(ErrorMessages.LaceInvalid, ContentFormatErrorCode.InvalidValue, absoluteOffset + cursor); }
        if (mode == MkvLacing.FixedSize) { Array.Fill(sizes, remaining / count); }
        else { sizes[^1] = remaining - total; }
        var frames = new BinarySegment[count];
        var offsets = new long[count];
        for (var i = 0; i < count; i++)
        {
            frames[i] = payload.Slice(cursor, sizes[i]);
            offsets[i] = absoluteOffset + cursor;
            cursor += sizes[i];
        }
        return new MkvBlock(track, timestamp, flags, simpleBlock, frames, offsets);
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

/// <summary>The RFC 9559 section 10.3 block lacing flags.</summary>
public enum MkvLacing
{
    /// <summary>A single frame.</summary>
    None = 0,
    /// <summary>Xiph lengths for all but the last frame.</summary>
    Xiph = 1,
    /// <summary>Equally sized frames.</summary>
    FixedSize = 2,
    /// <summary>Unsigned first size and signed EBML size differences.</summary>
    Ebml = 3
}
