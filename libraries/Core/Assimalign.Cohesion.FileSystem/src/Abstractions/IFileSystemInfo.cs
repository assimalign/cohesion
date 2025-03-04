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
}