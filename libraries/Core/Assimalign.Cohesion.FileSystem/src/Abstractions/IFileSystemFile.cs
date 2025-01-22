using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemFile : IFileSystemInfo, IDisposable
{
    /// <summary>
    /// The length of the file in bytes, or -1 for  non-existing files.
    /// </summary>
    Size Size { get; }
    /// <summary>
    /// The containing directory of the file.
    /// </summary>
    IFileSystemDirectory Directory { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IFileSystemChangeToken Watch();
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
    
    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="buffer"></param>
    ///// <param name="offset"></param>
    ///// <returns></returns>
    //int Read(Span<byte> buffer, long offset);
    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="buffer"></param>
    ///// <param name="offset"></param>
    ///// <returns></returns>
    //ValueTask<int> ReadAsync(Span<byte> buffer, long offset);
    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="buffer"></param>
    ///// <param name="offset"></param>
    //void Write(Span<byte> buffer, long offset);
    ///// <summary>
    ///// 
    ///// </summary>
    ///// <param name="buffer"></param>
    ///// <param name="offset"></param>
    ///// <returns></returns>
    //ValueTask WriteAsync(Span<byte> buffer, long offset);
}