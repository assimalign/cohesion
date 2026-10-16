using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>Deterministic PDF save choices and bounded structural buffering (§7.5).</summary>
public sealed class PdfWriterOptions
{
    /// <summary>Creates options for a full save with a classic cross-reference table.</summary>
    public PdfWriterOptions() { }
    /// <summary>Gets whether full saves use a cross-reference stream; requires PDF 1.5 or later.</summary>
    public bool UseCrossReferenceStream { get; init; }
    /// <summary>Gets whether full saves pack eligible objects; requires UseCrossReferenceStream.</summary>
    public bool PackObjects { get; init; }
    /// <summary>Gets whether replaced stream data is Flate encoded; default true. Existing encoded data is always copied.</summary>
    public bool CompressModifiedStreams { get; init; } = true;
    /// <summary>Gets the cap for each buffered object stream and xref encoding; default 16 MiB.</summary>
    public int MaxBufferedBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum object count, full-save Size, and direct nodes per object; default 1,000,000.</summary>
    public int MaxObjects { get; init; } = 1_000_000;
    /// <summary>Gets the maximum direct container depth; default 128, at most 512.</summary>
    public int MaxDepth { get; init; } = 128;
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBufferedBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 512);
        if (PackObjects && !UseCrossReferenceStream) { throw new ArgumentException(ErrorMessages.PackingNeedsStream); }
    }
}
