using System;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>A preceding sync sample, a target sample, and one contiguous source range for decoding through the target.</summary>
/// <remarks>The range can contain other tracks and headers. It assumes initialization metadata is already
/// available and does not constitute a standalone playable file. HTTP's inclusive end is ByteOffset + ByteLength - 1
/// for a nonempty range. Default is not a valid result; queries return null for absence.</remarks>
public readonly struct MediaSeekResult : IEquatable<MediaSeekResult>
{
    /// <summary>Creates a seek result, validating the samples and range endpoints.</summary>
    /// <param name="syncSample">The independent sample from which decoding starts.</param>
    /// <param name="targetSample">The sample found by presentation time, at or after the sync index.</param>
    /// <param name="byteOffset">The absolute start of the contiguous fetch range.</param>
    /// <param name="byteLength">The range length; the end is exclusive.</param>
    /// <exception cref="ArgumentException">Samples are invalid, mismatched, unordered, or outside the range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The byte range is negative or exceeds Int64 coordinates.</exception>
    /// <remarks>Only the profile can prove the range covers every dependency, intermediate sample, and required header.</remarks>
    public MediaSeekResult(MediaSample syncSample, MediaSample targetSample, long byteOffset, long byteLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteLength, long.MaxValue - byteOffset);
        if (syncSample.TrackId == 0 || !syncSample.IsSync || syncSample.TrackId != targetSample.TrackId ||
            syncSample.Index > targetSample.Index || syncSample.PresentationTime > targetSample.PresentationTime)
        {
            throw new ArgumentException(ErrorMessages.InvalidSeekSamples, nameof(syncSample));
        }
        var end = byteOffset + byteLength;
        if (byteOffset > syncSample.ByteOffset || byteOffset > targetSample.ByteOffset ||
            end < syncSample.ByteOffset + syncSample.ByteLength || end < targetSample.ByteOffset + targetSample.ByteLength)
        {
            throw new ArgumentException(ErrorMessages.IncompleteSeekRange, nameof(byteLength));
        }
        SyncSample = syncSample;
        TargetSample = targetSample;
        ByteOffset = byteOffset;
        ByteLength = byteLength;
    }

    /// <summary>Gets the preceding sync sample.</summary>
    public MediaSample SyncSample { get; }

    /// <summary>Gets the target sample found by presentation time.</summary>
    public MediaSample TargetSample { get; }

    /// <summary>Gets the absolute source offset to fetch.</summary>
    public long ByteOffset { get; }

    /// <summary>Gets the number of source bytes to fetch.</summary>
    public long ByteLength { get; }

    /// <inheritdoc/>
    public bool Equals(MediaSeekResult other) => SyncSample.Equals(other.SyncSample) &&
        TargetSample.Equals(other.TargetSample) && ByteOffset == other.ByteOffset && ByteLength == other.ByteLength;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MediaSeekResult other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(SyncSample, TargetSample, ByteOffset, ByteLength);
}
