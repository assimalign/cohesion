using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;


public class FileSystemChangeArgs<T>
{
    public FileSystemChangeArgs(FileSystemPath path, T state)
    {
        Path = path;
        State = state;
    }
    public FileSystemPath Path { get; }
    public T State { get; }
}

public class FileSystemRenameArgs<T>
{
    public FileSystemRenameArgs(FileSystemPath oldPath, FileSystemPath newPath, T state)
    {
        OldPath = oldPath;
        NewPath = newPath;
        State = state;
    }
    public FileSystemPath OldPath { get; }
    public FileSystemPath NewPath { get; }
    public T State { get; }
}

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
    IDisposable OnChange<T>(Action<FileSystemChangeArgs<T>> callback, T state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnCreate<T>(Action<FileSystemChangeArgs<T>> callback, T state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnDelete<T>(Action<FileSystemChangeArgs<T>> callback, T state);

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="callback"></param>
    /// <param name="state"></param>
    /// <returns></returns>
    IDisposable OnRename<T>(Action<FileSystemRenameArgs<T>> callback, T state);
}