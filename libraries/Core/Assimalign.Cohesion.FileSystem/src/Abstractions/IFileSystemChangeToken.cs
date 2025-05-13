using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemChangeToken : IChangeToken
{
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnChange<T>(Action<T> callback, T state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnCreate<T>(Action<T> callback, T state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnDelete<T>(Action<T> callback, T state);
}