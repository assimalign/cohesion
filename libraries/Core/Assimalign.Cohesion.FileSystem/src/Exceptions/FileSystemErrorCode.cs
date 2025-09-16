namespace Assimalign.Cohesion.FileSystem;

public enum FileSystemErrorCode
{
    /// <summary>
    /// 
    /// </summary>
    Other,

    /// <summary>
    /// Occurs when an <see cref="IFileSystemInfo"/> is not found.
    /// </summary>
    NotFound,

    /// <summary>
    /// The target location already exists.
    /// </summary>
    Conflict,

    /// <summary>
    /// The file or directory path is too long.
    /// </summary>
    PathTooLong,

    /// <summary>
    /// 
    /// </summary>
    Unauthorized,
}
