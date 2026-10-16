namespace Assimalign.Cohesion.Content.Media;

/// <summary>Describes container-declared video geometry for a track of kind Video.</summary>
public interface IMediaVideoTrack : IMediaTrack
{
    /// <summary>Gets the positive encoded image width in pixels, or null when unknown.</summary>
    ulong? PixelWidth { get; }

    /// <summary>Gets the positive encoded image height in pixels, or null when unknown.</summary>
    ulong? PixelHeight { get; }

    /// <summary>Gets the positive width-to-height ratio of the displayed image before rotation,
    /// incorporating container crop and pixel-aspect metadata, or null when unknown.</summary>
    (ulong Width, ulong Height)? DisplayAspectRatio { get; }
}
