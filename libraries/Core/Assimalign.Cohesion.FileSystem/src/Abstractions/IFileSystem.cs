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
    /// The max size of the file system.
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
    /// A user friendly name for the file system.
    /// </summary>
    string Name { get; }

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
    /// Returns a <see cref="IFileSystemEventToken"/> for the specified <paramref name="pattern"/>.
    /// Examples: 
    /// <list type="bullet">
    /// <item>
    ///     <term>C:\Users\johndoe\Documents\**\*.cs</term>
    ///     <description>test</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="pattern">Filter string used to determine what files or folders to monitor. Example: **/*.cs, *.*, subFolder/**/*.cshtml.</param>
    /// <returns>An <see cref="IFileSystemEventToken"/> that is notified when a file matching <paramref name="filter"/> is added, modified or deleted.</returns>
    IFileSystemEventToken Watch(Glob? pattern);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = default);

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
    /// Returns the base info of either 
    /// a <see cref="IFileSystemFile"/> or <see cref="IFileSystemDirectory"/>.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemInfo GetInfo(FileSystemPath path);

    /// <summary>
    /// Creates a directory at the given path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemDirectory CreateDirectory(FileSystemPath path);

    /// <summary>
    /// Creates a file at the given path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile CreateFile(FileSystemPath path);

    /// <summary>
    /// Deletes a directory from the file system.
    /// </summary>
    /// <param name="path"></param>
    void DeleteDirectory(FileSystemPath path);

    /// <summary>
    /// Deletes a file from the file system.
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