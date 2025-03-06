namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public interface IFilePathSegment
{
    /// <summary>
    /// 
    /// </summary>
    bool HasStem { get; }

    /// <summary>
    /// The kind of segment.
    /// </summary>
    SegmentKind Kind { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    bool Match(string value);
}


public enum SegmentKind
{
    None,
    Literal,
    Wildcard,
    RecursiveWildcard,
    ParentDirectory,
}