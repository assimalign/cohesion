using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemFile : IFileSystemInfo, IAsyncDisposable
{
    /// <summary>
    /// The length of the file in bytes, or -1 for  non-existing files.
    /// </summary>
    Size Size { get; }
    /// <summary>
    /// The containing directory of the file.
    /// </summary>
    IFileSystemDirectory Directory { get; }
    void Open();
    void Open(FileMode mode);
    void Open(FileMode mode, FileAccess access);
    void Open(FileMode mode, FileAccess access, FileShare share);


    /// <summary>
    /// Return file content as readonly stream. Caller should dispose stream when complete.
    /// </summary>
    /// <returns>The file stream</returns>
    Stream Stream { get; }
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    int Read(Span<byte> buffer, long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="position"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    int Read(Span<byte> buffer, long position, long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="position"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    ValueTask<int> ReadAsync(Span<byte> buffer, long position, long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="offset"></param>
    void Write(Span<byte> buffer, long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="position"></param>
    /// <param name="offset"></param>
    void Write(Span<byte> buffer, long position, long offset);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="buffer"></param>
    /// <param name="position"></param>
    /// <param name="offset"></param>
    /// <returns></returns>
    ValueTask WriteAsync(Span<byte> buffer, long position, long offset);
}