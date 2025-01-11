using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystem : IEnumerable<IFileSystemInfo>, IDisposable, IAsyncDisposable
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
    bool Exist(FileSystemPath path);
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
    /// Returns the directory at the given path.
    /// </summary>
    /// <param name="path">The path of the directory.</param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(FileSystemPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile GetFile(FileSystemPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemDirectory CreateDirectory(FileSystemPath path);
    /// <summary>
    ///
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile CreateFile(FileSystemPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    void DeleteDirectory(FileSystemPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    void DeleteFile(FileSystemPath path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    void CopyFile(FileSystemPath source, FileSystemPath destination);
    /// <summary>
    /// Moves a directory or file the the provided destination.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="destination"></param>
    void Move(FileSystemPath source, FileSystemPath destination);
}