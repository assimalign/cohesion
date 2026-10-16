using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;

namespace Assimalign.Cohesion.Content.Media;

/// <summary>
/// Presents encoded tracks and their shared presentation timeline over binary content.
/// </summary>
/// <remarks>Tracks, indexes, and lazy codec data borrow this file's source and are valid only while
/// it remains open and its byte layout is unchanged. Implementations define snapshot/refresh behavior
/// for live sources. Track discovery must be bounded; sample tables and payloads remain lazy.
/// Tracks are descriptions, not independently serialized IContent children.</remarks>
public interface IMediaFile : IBinaryFile
{
    /// <summary>Gets the discovered tracks in container order, with unique identifiers within this file.</summary>
    IReadOnlyList<IMediaTrack> Tracks { get; }

    /// <summary>Gets the presentation duration, or null when unknown. Unknown does not imply live.</summary>
    MediaTimestamp? Duration { get; }

    /// <summary>Gets whether more media is expected from an ongoing presentation; this does not imply seekability.</summary>
    bool IsLive { get; }
}
