namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// Diagnostics codes attached to <see cref="FileSystemException"/>. Implementations of
/// <see cref="IFileSystem"/> map their failure modes onto these codes so consumers can branch
/// on the exact cause without text matching.
/// </summary>
public enum FileSystemErrorCode
{
    /// <summary>
    /// Unclassified file system error. Avoid in new code; pick a more specific value.
    /// </summary>
    Other,

    /// <summary>
    /// A required <see cref="IFileSystemInfo"/> (file or directory) was not found at the
    /// requested path.
    /// </summary>
    NotFound,

    /// <summary>
    /// The target location already exists. Used by create and move operations when the
    /// destination is occupied.
    /// </summary>
    Conflict,

    /// <summary>
    /// The supplied path exceeds the underlying provider's maximum length.
    /// </summary>
    PathTooLong,

    /// <summary>
    /// The file system cannot satisfy the operation because not enough storage space is
    /// available.
    /// </summary>
    NotEnoughSpace,

    /// <summary>
    /// The caller does not have permission to access the target path.
    /// </summary>
    AccessDenied,

    /// <summary>
    /// The path is currently held open by another consumer that has not granted compatible
    /// share access.
    /// </summary>
    PathInUse,

    /// <summary>
    /// The file system is configured read-only and rejects the requested mutation.
    /// </summary>
    ReadOnly,
}
