using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>Describes one encoded track without interpreting its codec bitstream.</summary>
/// <remarks>Optional metadata is null when unavailable, never guessed by parsing codec bytes.
/// Codec fields describe the initial configuration; configuration changes remain profile-owned.
/// All values and the sample index borrow the owning file's lifetime.</remarks>
public interface IMediaTrack
{
    /// <summary>Gets the nonzero identifier unique within the presentation (not a list index).</summary>
    ulong Id { get; }

    /// <summary>Gets the track's purpose.</summary>
    MediaTrackKind Kind { get; }

    /// <summary>Gets the native codec identifier, such as avc1 or V_MPEG4/ISO/AVC; it is not a MIME codec string.</summary>
    string CodecId { get; }

    /// <summary>Gets opaque initialization bytes, preferably a lazy segment; default means no private data.</summary>
    BinarySegment CodecPrivateData { get; }

    /// <summary>Gets the language, using BCP 47 when supplied by the format, or null when unspecified.
    /// Legacy language codes are preserved when no BCP 47 tag is supplied.</summary>
    string? Language { get; }

    /// <summary>Gets positive ticks per second for this view's media clock. Individual timestamps may
    /// use other scales to preserve exact presentation mappings.</summary>
    long Timescale { get; }

    /// <summary>Gets the track's duration on the presentation timeline, or null when unknown.</summary>
    MediaTimestamp? Duration { get; }

    /// <summary>Gets whether the track is enabled for use.</summary>
    bool IsEnabled { get; }

    /// <summary>Gets eligibility for automatic selection, or null when the format/profile supplies no such decision.</summary>
    bool? IsDefault { get; }

    /// <summary>Gets the lazy index for this track; its TrackId must equal Id.</summary>
    IMediaSampleIndex Samples { get; }
}
