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
    /// The number of items in the directory.
    /// </summary>
    //long Count { get; }
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
    /// 
    /// </summary>
    /// <param name="path">A relative path to given directory.</param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(FileSystemPath path);
    /// <summary>
    /// Get all files in contained in the directory.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemInfo> GetFiles();
    /// <summary>
    ///  Returns the <see cref="IFileSystemInfo"/> for a given file 
    ///  from the current location of a given directory.
    /// </summary>
    /// <param name="path">The name of the file.</param>
    /// <returns></returns>
    IFileSystemFile GetFile(FileSystemPath path);
}