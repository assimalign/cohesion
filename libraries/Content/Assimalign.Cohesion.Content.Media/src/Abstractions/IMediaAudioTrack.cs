namespace Assimalign.Cohesion.Content.Media;

/// <summary>Describes container-declared audio properties for a track of kind Audio.</summary>
public interface IMediaAudioTrack : IMediaTrack
{
    /// <summary>Gets the finite positive sampling frequency in hertz, or null when unknown.
    /// This descriptor preserves container floating-point/fixed-point rates; timeline math uses MediaTimestamp.</summary>
    double? SampleRate { get; }

    /// <summary>Gets the positive channel count, or null when unknown.</summary>
    ulong? ChannelCount { get; }
}
