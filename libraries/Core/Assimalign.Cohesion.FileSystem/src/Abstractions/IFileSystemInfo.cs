using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemInfo
{
    /// <summary>
    /// The path to the file or directory.
    /// </summary>
    FileSystemPath Path { get; }

    /// <summary>
    /// When the file was created.
    /// </summary>
    DateTime CreatedOn { get; }

    /// <summary>
    /// When the file was last modified.
    /// </summary>
    DateTime UpdatedOn { get; }

    /// <summary>
    /// The last time the info was accessed.
    /// </summary>
    DateTime AccessedOn { get; }

    /// <summary>
    /// The attributes of the file or directory.
    /// </summary>
    FileAttributes Attributes { get; }

    /// <summary>
    /// Returns the file system reference the <see cref="IFileSystemInfo"/> belongs to.
    /// </summary>
    IFileSystem FileSystem { get; }

    /// <summary>
    /// Adds or updates the file or directory attributes.
    /// </summary>
    /// <param name="attributes"></param>
    void SetAttributes(FileAttributes attributes);
}