using Assimalign.Cohesion.Content.Bmff;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>Limits and profile-conformance policy for an MP4 media view.</summary>
public sealed class Mp4ReaderOptions
{
    /// <summary>Gets the BMFF parsing limits and unknown-box policy.</summary>
    public BmffReaderOptions ContainerOptions { get; init; } = new();
    /// <summary>Gets whether contradictory brands throw InvalidStructure; otherwise they become diagnostics.</summary>
    public bool Strict { get; init; } = true;
    /// <summary>Gets the track-discovery cap; defaults to 1024.</summary>
    public int MaxTrackCount { get; init; } = 1024;
    /// <summary>Gets the cap on retained fragment runs across all tracks; defaults to one million.</summary>
    public int MaxFragmentRunCount { get; init; } = 1_000_000;
}
