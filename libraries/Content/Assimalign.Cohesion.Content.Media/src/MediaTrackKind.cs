namespace Assimalign.Cohesion.Content.Media;

/// <summary>
/// The purpose of an encoded track, independent of its container or codec.
/// </summary>
public enum MediaTrackKind
{
    /// <summary>A track outside the recognized kinds.</summary>
    Other,

    /// <summary>Moving images.</summary>
    Video,

    /// <summary>Audio samples.</summary>
    Audio,

    /// <summary>Text, subtitles, or closed captions, including bitmap subtitles.</summary>
    Text,

    /// <summary>Timed metadata.</summary>
    Metadata
}
