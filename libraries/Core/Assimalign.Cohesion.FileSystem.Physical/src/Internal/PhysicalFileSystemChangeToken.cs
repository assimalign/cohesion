using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class PhysicalFileSystemChangeToken : IFileSystemEventToken, IDisposable
{
    // TODO: Need to create a file system polling object. FileSystemWatcher is only available on Windows.
    private readonly Glob _glob;
    private readonly FileSystemWatcher _watcher;
    private readonly PhysicalFileSystemInfo _fileSystemInfo;
    private readonly List<Subscriber> _subscribers;

    public PhysicalFileSystemChangeToken(PhysicalFileSystemInfo fileSystemInfo, Glob glob)
    {
        _fileSystemInfo = fileSystemInfo;
        _glob = glob;
        _subscribers = new List<Subscriber>();
        _watcher = new FileSystemWatcher(fileSystemInfo.Path);
        _watcher.EnableRaisingEvents = true;
        _watcher.IncludeSubdirectories = true;
        _watcher.Created += (sender, args) => Notify(sender, args, ChangeType.Created);
        _watcher.Deleted += (sender, args) => Notify(sender, args, ChangeType.Deleted);
        _watcher.Changed += (sender, args) => Notify(sender, args, ChangeType.Changed);
        _watcher.Renamed += (sender, args) => Notify(sender, args, ChangeType.Rename);
    }

    public void Dispose()
    {
        
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        return OnChange(callback, state);
    }
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T?>()
        {
            ChangeType = ChangeType.Changed,
            State = state,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T?>()
        {
            ChangeType = ChangeType.Created,
            State = state,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber<T?>()
        {
            ChangeType = ChangeType.Deleted,
            State = state,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T>> callback, T state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new RenameSubscriber<T>()
        {
            ChangeType = ChangeType.Rename,
            State = state,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }

    private void Notify(object sender, FileSystemEventArgs args, ChangeType changeType)
    {
        FileSystemPath fileSystemPath = args.FullPath;
        IFileSystem fileSystem = _fileSystemInfo.FileSystem;

        if (!_glob.IsMatch(fileSystemPath))
        {
            return;
        }

        IFileSystemInfo fileSystemInfo = fileSystem.GetInfo(fileSystemPath);

        foreach (var subscriber in _subscribers)
        {
            if (subscriber.ChangeType == changeType)
            {
                subscriber.Invoke(args);
            }
        }
    }
    enum ChangeType
    {
        Created,
        Deleted,
        Changed,
        Rename
    }

    abstract partial class Subscriber : IDisposable
    {
        public required ChangeType ChangeType { get; init; }
        public Action<Subscriber> OnDispose { get; init; } = default!;
        public abstract void Invoke(FileSystemEventArgs args);
        public void Dispose()
        {
            OnDispose.Invoke(this);
        }
    }

    partial class Subscriber<T> : Subscriber
    {
        public required T State { get; init; }
        public required Action<FileSystemEvent<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            Callback.Invoke(new FileSystemEvent<T>(args.FullPath, State));
        }
    }

    partial class RenameSubscriber<T> : Subscriber
    {
        public required T State { get; init; }
        public required Action<FileSystemRenameEvent<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            var renameArgs = (RenamedEventArgs)args;

            Callback.Invoke(new FileSystemRenameEvent<T>(renameArgs.OldFullPath, renameArgs.FullPath, State));
        }
    }
}