using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Mkv.Internal;

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
        var header = MkvBlockHeader.Read(input, payload.Length, absoluteOffset, simpleBlock, maxFrames);
        var frames = new BinarySegment[header.Sizes.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            frames[i] = payload.Slice(header.Offsets[i] - absoluteOffset, header.Sizes[i]);
        }
        return new MkvBlock(header.TrackNumber, header.RelativeTimestamp, header.Flags, simpleBlock, frames, header.Offsets);
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
