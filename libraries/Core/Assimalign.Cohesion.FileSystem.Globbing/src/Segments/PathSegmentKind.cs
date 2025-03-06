namespace Assimalign.Cohesion.FileSystem;

public enum PathSegmentKind
{
    /// <summary>
    /// 
    /// </summary>
    Current,
    /// <summary>
    /// 
    /// </summary>
    Literal,
    /// <summary>
    /// * - Matches any character in a filename.
    /// </summary>
    Wildcard,
    /// <summary>
    /// ** - Matches any character in a filename or directory.
    /// </summary>
    RecursiveWildcard,
    /// <summary>
    /// .. - Matches the parent directory.
    /// </summary>
    ParentDirectory
}
