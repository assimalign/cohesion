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
    /// Generates a change token for the given glob pattern relative to the current directory.
    /// <list type="bullet">
    /// <item>
    /// <term>"."</term>
    /// <description>Will watch the current directory</description>
    /// </item>
    /// </list>
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    IFileSystemEventToken Watch(Glob? pattern);

    /// <summary>
    /// Gets all the directories relative to the current directory.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemDirectory> GetDirectories();

    /// <summary>
    /// Gets an existing directory.
    /// </summary>
    /// <param name="name">A relative or absolute path to given directory.</param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(DirectoryName name);

    /// <summary>
    /// Gets all the files relative to the current directory.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemFile> GetFiles();

    /// <summary>
    ///  Returns the <see cref="IFileSystemInfo"/> for a given file 
    ///  from the current location of a given directory.
    /// </summary>
    /// <param name="name">The name of the file.</param>
    /// <returns></returns>
    IFileSystemFile GetFile(FileName name);

    /// <summary>
    /// Creates a directory relative to the current directory.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    IFileSystemDirectory CreateDirectory(DirectoryName name);

    /// <summary>
    /// Creates a file relative to the current directory.
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    IFileSystemFile CreateFile(FileName name);

    /// <summary>
    /// Deletes a directory relative to the current directory.
    /// </summary>
    /// <param name="name"></param>
    void DeleteDirectory(DirectoryName name);

    /// <summary>
    /// Deletes a file relative to the current directory.
    /// </summary>
    /// <param name="name"></param>
    void DeleteFile(FileName name);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = default);
}