using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemFile : IFileSystemInfo
{
    /// <summary>
    /// The length of the file in bytes, or -1 for  non-existing files.
    /// </summary>
    Size Size { get; }

    /// <summary>
    /// The name of the file.
    /// </summary>
    FileName Name { get; }

    /// <summary>
    /// The containing directory of the file.
    /// </summary>
    IFileSystemDirectory Directory { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IFileSystemEventToken Watch();

    /// <summary>
    /// Return file content as readonly stream. Caller should dispose stream when complete.
    /// </summary>
    /// <returns>The file stream</returns>
    Stream Open();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="fileMode"></param>
    /// <returns></returns>
    Stream Open(FileMode fileMode);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="fileMode"></param>
    /// <param name="fileAccess"></param>
    /// <returns></returns>
    Stream Open(FileMode fileMode, FileAccess fileAccess);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="fileMode"></param>
    /// <param name="fileAccess"></param>
    /// <param name="fileShare"></param>
    /// <returns></returns>
    Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare);

    /// <summary>
    /// Opens the file for random-access, optionally durable I/O — the shape a storage engine needs:
    /// positional reads and writes at an offset, and a flush that can be guaranteed to reach the
    /// durable medium.
    /// </summary>
    /// <param name="fileMode">How the file is opened or created.</param>
    /// <param name="fileAccess">Whether the handle reads, writes, or both.</param>
    /// <param name="fileShare">How other handles may share the file.</param>
    /// <returns>A handle the caller owns and must dispose.</returns>
    IFileSystemFileHandle OpenHandle(FileMode fileMode, FileAccess fileAccess, FileShare fileShare);
}
