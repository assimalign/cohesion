using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemInfo
{
    /// <summary>
    /// The name of the file or directory.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The path to the file, including the file name.
    /// </summary>
    FileSystemPath Path { get; }
    /// <summary>
    /// When the file was last modified.
    /// </summary>
    DateTimeOffset UpdatedOn { get; }
    /// <summary>
    /// When the file was created.
    /// </summary>
    DateTimeOffset CreatedOn { get; }
    /// <summary>
    /// The last time the info was accessed.
    /// </summary>
    DateTimeOffset AccessedOn { get; }
}