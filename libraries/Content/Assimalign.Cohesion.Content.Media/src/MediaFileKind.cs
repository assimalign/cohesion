namespace Assimalign.Cohesion.Content.Media;

/// <summary>
/// The kind of media a media file carries.
/// </summary>
public enum MediaFileKind
{
    /// <summary>Video media, possibly with audio tracks.</summary>
    Video,

    /// <summary>Audio-only media.</summary>
    Audio,

    /// <summary>Still-image media.</summary>
    Image,

    /// <summary>Media that is none of the other kinds.</summary>
    Other
}
