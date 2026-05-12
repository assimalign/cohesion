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
    /// An exception when there is not enough space within the given file system.
    /// </summary>
    NotEnoughSpace,

    /// <summary>
    /// 
    /// </summary>
    AccessDenied,

    /// <summary>
    /// 
    /// </summary>
    PathInUse,
}
