using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemChangeToken : IChangeToken<IFileSystemChangeContext>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnCreate(Action<IFileSystemChangeContext> callback);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnDelete(Action<IFileSystemChangeContext> callback);
}


public interface IFileSystemChangeContext
{
    /// <summary>
    /// The effected path
    /// </summary>
    FileSystemPath Path { get; }

    /// <summary>
    /// The file system info the 
    /// </summary>
    IFileSystemInfo Info { get; }
}
