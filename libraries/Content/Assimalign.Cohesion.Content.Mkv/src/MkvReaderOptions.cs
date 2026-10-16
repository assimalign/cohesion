using System;

using Assimalign.Cohesion.Content.Ebml;

namespace Assimalign.Cohesion.Content.Mkv;

/// <summary>Bounds Matroska metadata, lacing and explicitly requested attachment buffering.</summary>
public sealed class MkvReaderOptions
{
    /// <summary>Creates bounded defaults.</summary>
    public MkvReaderOptions() { }
    /// <summary>Gets or sets the EBML depth, node and value limits. Lenient loading is not supported by the media adapter.</summary>
    public EbmlReaderOptions Ebml { get; set; } = new();
    /// <summary>Gets or sets the maximum tracks, initially 256.</summary>
    public int MaxTracks { get; set; } = 256;
    /// <summary>Gets or sets the maximum cue track positions, initially 100,000.</summary>
    public int MaxCuePoints { get; set; } = 100_000;
    /// <summary>Gets or sets the maximum frames per block, initially the format maximum of 256.</summary>
    public int MaxLaceFrames { get; set; } = 256;
    /// <summary>Gets or sets the attachment materialization cap, initially 16 MiB. Lazy attachment size is unlimited.</summary>
    public int MaxAttachmentBufferLength { get; set; } = 16 * 1024 * 1024;

    internal MkvReaderOptions Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Ebml);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTracks, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCuePoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLaceFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxLaceFrames, 256);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxAttachmentBufferLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAttachmentBufferLength, Array.MaxLength);
        if (Ebml.Lenient) { throw new ArgumentException(ErrorMessages.StrictRequired, nameof(Ebml)); }
        return new MkvReaderOptions
        {
            MaxTracks = MaxTracks, MaxCuePoints = MaxCuePoints, MaxLaceFrames = MaxLaceFrames,
            MaxAttachmentBufferLength = MaxAttachmentBufferLength,
            Ebml = new EbmlReaderOptions
            {
                MaxDepth = Ebml.MaxDepth, MaxElementCount = Ebml.MaxElementCount,
                MaxBufferedValueLength = Ebml.MaxBufferedValueLength, VerifyCrc32 = Ebml.VerifyCrc32,
                MaxDiagnostics = Ebml.MaxDiagnostics, MaxIdLength = Ebml.MaxIdLength, MaxSizeLength = Ebml.MaxSizeLength
            }
        };
    }
}
