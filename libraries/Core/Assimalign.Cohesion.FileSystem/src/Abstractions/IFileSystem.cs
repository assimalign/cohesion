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
    /// The total size of the file system.
    /// </summary>
    Size Size { get; }

    /// <summary>
    /// The available space.
    /// </summary>
    Size SpaceAvailable { get; }

    /// <summary>
    /// The amount of space used.
    /// </summary>
    Size SpaceUsed { get; }

    /// <summary>
    /// Indicates whether the file system is read only.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// The root directory of the file system.
    /// </summary>
    IFileSystemDirectory RootDirectory { get; }

    /// <summary>
    /// Checks whether the given path exists in the file system.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool Exists(FileSystemPath path);

    /// <summary>
    /// Creates a <see cref="IFileSystemChangeToken"/> for the specified <paramref name="filter"/>.
    /// Examples: 
    /// <list type="bullet">
    /// <item>
    ///     <term>C:\Users\johndoe\Documents\**\*.cs</term>
    ///     <description>test</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="pattern">Filter string used to determine what files or folders to monitor. Example: **/*.cs, *.*, subFolder/**/*.cshtml.</param>
    /// <returns>An <see cref="IFileSystemChangeToken"/> that is notified when a file matching <paramref name="filter"/> is added, modified or deleted.</returns>
    IFileSystemChangeToken Watch(Glob? pattern);

    /// <summary>
    /// Enumerates all the files in the file system.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemFile> EnumerateFiles();

    /// <summary>
    /// Enumerates through all the directories and sub-directories in the file system.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemDirectory> EnumerateDirectories();

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
    /// Copies a file one location to another.
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