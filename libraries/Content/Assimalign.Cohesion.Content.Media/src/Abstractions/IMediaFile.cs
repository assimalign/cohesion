using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>
/// Represents audio, video, or image media content composed of child structures such as tracks.
/// </summary>
public interface IMediaFile : IBinaryFile, IComposableContent
{
    /// <summary>Gets the kind of media the file carries.</summary>
    MediaFileKind Kind { get; }
}
