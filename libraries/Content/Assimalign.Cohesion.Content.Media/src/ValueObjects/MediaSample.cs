using System;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>Describes one encoded sample/frame without loading its bytes.</summary>
/// <remarks>Byte coordinates refer to the owning file's unchanged source, with end exclusive.
/// A laced block is expanded lazily into individual frames. Index is decode/source order, not presentation
/// order. Default has track zero and is not a valid sample; absence is represented by a nullable sample.</remarks>
public readonly struct MediaSample : IEquatable<MediaSample>
{
    /// <summary>Creates a sample description and computes its effective presentation offset when decode time is known.</summary>
    /// <param name="trackId">The nonzero track identifier.</param>
    /// <param name="index">The nonnegative zero-based decode/source-order index.</param>
    /// <param name="decodeTime">The media decode time, or null if the container provides no recoverable DTS.</param>
    /// <param name="presentationTime">The effective time on the file's presentation timeline.</param>
    /// <param name="byteOffset">The nonnegative absolute payload offset in the source.</param>
    /// <param name="byteLength">The nonnegative payload length.</param>
    /// <param name="isSync">Whether container metadata identifies an independent random access point.</param>
    /// <exception cref="ArgumentOutOfRangeException">The track, index, or byte range is invalid.</exception>
    /// <exception cref="OverflowException">The exact presentation offset cannot be represented.</exception>
    public MediaSample(ulong trackId, long index, MediaTimestamp? decodeTime, MediaTimestamp presentationTime,
        long byteOffset, long byteLength, bool isSync)
    {
        ArgumentOutOfRangeException.ThrowIfZero(trackId);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteLength, long.MaxValue - byteOffset);
        TrackId = trackId;
        Index = index;
        DecodeTime = decodeTime;
        PresentationTime = presentationTime;
        PresentationOffset = decodeTime is { } time ? presentationTime - time : null;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        IsSync = isSync;
    }

    /// <summary>Gets the owning track identifier.</summary>
    public ulong TrackId { get; }

    /// <summary>Gets the zero-based decode/source-order sample index.</summary>
    public long Index { get; }

    /// <summary>Gets the media decode timestamp, or null when unavailable. PTS is never substituted for missing DTS.</summary>
    public MediaTimestamp? DecodeTime { get; }

    /// <summary>Gets the effective presentation timestamp after profile timeline mapping.</summary>
    public MediaTimestamp PresentationTime { get; }

    /// <summary>Gets PresentationTime minus DecodeTime, or null when DTS is unknown.
    /// This combines composition and presentation mapping offsets; it is not a raw ctts entry.</summary>
    public MediaTimestamp? PresentationOffset { get; }

    /// <summary>Gets the absolute offset of the encoded payload.</summary>
    public long ByteOffset { get; }

    /// <summary>Gets the encoded payload length, excluding container headers.</summary>
    public long ByteLength { get; }

    /// <summary>Gets whether the sample is a container-declared sync/keyframe.</summary>
    public bool IsSync { get; }

    /// <inheritdoc/>
    public bool Equals(MediaSample other) => TrackId == other.TrackId && Index == other.Index &&
        DecodeTime == other.DecodeTime && PresentationTime == other.PresentationTime &&
        ByteOffset == other.ByteOffset && ByteLength == other.ByteLength && IsSync == other.IsSync;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MediaSample other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(TrackId, Index, DecodeTime, PresentationTime, ByteOffset, ByteLength, IsSync);
}
