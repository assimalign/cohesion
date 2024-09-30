using System;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemInfo
{
    /// <summary>
    /// The name of the file or directory, not including any path.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The path to the file, including the file name. Return null if the file is not directly accessible.
    /// </summary>
    Path Path { get; }
    /// <summary>
    /// When the file was last modified.
    /// </summary>
    DateTimeOffset UpdatedDateTime { get; }
    /// <summary>
    /// When the file was created.
    /// </summary>
    DateTimeOffset CreatedDateTime { get; }
}