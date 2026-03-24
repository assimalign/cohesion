using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public interface IFileSystemEventToken : IChangeToken
{
    /// <summary>
    /// Registers for a callback that will be invoked when a file is changed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state);

    /// <summary>
    /// Registers for a callback that will be invoked when a file or directory is created.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state);

    /// <summary>
    /// Registeres a disposable callback to be invoked when a file or directory is deleted.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state);

    /// <summary>
    /// Registers a disposable callback to be invoked when a file or directory is renamed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state);
}