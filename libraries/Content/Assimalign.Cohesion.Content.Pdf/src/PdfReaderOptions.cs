using System;

namespace Assimalign.Cohesion.Content.Pdf;

/// <summary>Resource limits and explicit recovery policy for hostile PDF input.</summary>
public sealed class PdfReaderOptions
{
    /// <summary>Creates strict options with bounded default limits.</summary>
    public PdfReaderOptions() { }
    /// <summary>Gets whether documented syntax and cross-reference recovery is enabled; default false.</summary>
    public bool IsLenient { get; init; }
    /// <summary>Gets the maximum bytes in a token or buffered encoded stream; default 16 MiB.</summary>
    public int MaxBufferedBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum output of each filter and predictor stage; default 16 MiB.</summary>
    public int MaxDecodedBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum nesting and resolution depth; default 128, at most 512.</summary>
    public int MaxDepth { get; init; } = 128;
    /// <summary>Gets the maximum cross-reference sections, including hybrid supplements; default 128.</summary>
    public int MaxCrossReferenceSections { get; init; } = 128;
    /// <summary>Gets the maximum retained xref entries, objects per container, and pages; default 1,000,000.</summary>
    public int MaxObjects { get; init; } = 1_000_000;
    /// <summary>Gets the bounded backward startxref search window; default 1 MiB.</summary>
    public int StartXrefSearchBytes { get; init; } = 1024 * 1024;
    /// <summary>Gets the maximum bytes scanned during lenient rebuilding or stream recovery; default 64 MiB.</summary>
    public long MaxRecoveryScanBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBufferedBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDecodedBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 512);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCrossReferenceSections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxObjects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(StartXrefSearchBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(StartXrefSearchBytes, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecoveryScanBytes);
    }
}
