using System;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemChangeToken : IChangeToken<IFileSystemInfo>
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnCreate(Action<IFileSystemInfo> callback);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnDelete(Action<IFileSystemInfo> callback);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnRename(Action<IFileSystemInfo> callback);
}
