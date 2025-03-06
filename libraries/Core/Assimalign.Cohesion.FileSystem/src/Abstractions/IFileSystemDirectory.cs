using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemDirectory : IFileSystemInfo, IEnumerable<IFileSystemInfo>
{
    /// <summary>
    /// The name of the directory.
    /// </summary>
    DirectoryName Name { get; }

    /// <summary>
    /// The parent directory.
    /// </summary>
    IFileSystemDirectory? Parent { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="filter"></param>
    /// <returns></returns>
    IFileSystemChangeToken Watch(string filter);

    /// <summary>
    /// Checks whether a relative path from the current directory exists.
    /// </summary>
    /// <param name="path"> a relative path.</param>
    /// <returns></returns>
    bool Exist(FileSystemPath path);

    /// <summary>
    /// Gets all the directories contained in the directory.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemDirectory> GetDirectories();

    /// <summary>
    /// Gets an existing directory.
    /// </summary>
    /// <param name="path">A relative  or absolute path to given directory.</param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(FileSystemPath path);

    /// <summary>
    /// Get all files in contained in the directory.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemFile> GetFiles();

    /// <summary>
    ///  Returns the <see cref="IFileSystemInfo"/> for a given file 
    ///  from the current location of a given directory.
    /// </summary>
    /// <param name="path">The name of the file.</param>
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