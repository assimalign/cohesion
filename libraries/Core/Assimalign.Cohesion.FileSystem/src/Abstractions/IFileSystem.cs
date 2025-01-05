using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystem : IEnumerable<IFileSystemInfo>, IDisposable
{
    /// <summary>
    /// The name of the file system.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The total size of the file system.
    /// </summary>
    Size Size { get; }
    /// <summary>
    /// The available space.
    /// </summary>
    Size Space { get; }
    /// <summary>
    /// The amount of space used.
    /// </summary>
    Size SpaceUsed { get; }
    /// <summary>
    /// The root directory of the file system.
    /// </summary>
    IFileSystemDirectory RootDirectory { get; }
    /// <summary>
    /// Checks whether the given path exists in the file system.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool Exist(Path path);
    /// <summary>
    /// Creates a <see cref="IChangeToken"/> for the specified <paramref name="filter"/>.
    /// </summary>
    /// <param name="filter">Filter string used to determine what files or folders to monitor. Example: **/*.cs, *.*, subFolder/**/*.cshtml.</param>
    /// <returns>An <see cref="IChangeToken"/> that is notified when a file matching <paramref name="filter"/> is added, modified or deleted.</returns>
    IFileSystemChangeToken Watch(string filter);
    /// <summary>
    /// Enumerates through all the directories in the file system.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemDirectory> GetDirectories();
    /// <summary>
    /// Enumerates all the files in the File System.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemFile> GetFiles();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile GetFile(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemDirectory CreateDirectory(Path path);
    /// <summary>
    ///
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile CreateFile(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    void DeleteDirectory(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    void DeleteFile(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    void CopyFile(Path source, Path destination);
    /// <summary>
    /// Moves a directory or file the the provided destination.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    void Move(Path source, Path destination);
}