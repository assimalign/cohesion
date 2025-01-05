using System;

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
    Path Path { get; }
    /// <summary>
    /// When the file was last modified.
    /// </summary>
    DateTimeOffset UpdatedOn { get; }
    /// <summary>
    /// When the file was created.
    /// </summary>
    DateTimeOffset CreatedOn { get; }
    /// <summary>
    /// 
    /// </summary>
    DateTimeOffset AccessedOn { get; }
}