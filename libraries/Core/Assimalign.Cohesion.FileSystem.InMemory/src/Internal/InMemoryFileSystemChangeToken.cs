using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Globbing;
using Assimalign.Cohesion.Internal;

internal class InMemoryFileSystemChangeToken : IFileSystemChangeToken
{
    // TODO: Need to create a file system polling object. FileSystemWatcher is only available on Windows.
    private readonly Glob _glob;
    private readonly InMemoryFileSystemInfo _fileSystemInfo;
    private readonly List<Subscriber> _subscribers;

    public InMemoryFileSystemChangeToken(InMemoryFileSystemInfo fileSystemInfo, Glob glob)
    {
        _fileSystemInfo = fileSystemInfo;
        _glob = glob;
        _subscribers = new List<Subscriber>();
        _fileSystemInfo.Dispatcher.Created += (sender, args) => Notify(sender, args, ChangeType.Created);
        _fileSystemInfo.Dispatcher.Deleted += (sender, args) => Notify(sender, args, ChangeType.Deleted);
        _fileSystemInfo.Dispatcher.Changed += (sender, args) => Notify(sender, args, ChangeType.Changed);
        _fileSystemInfo.Dispatcher.Renamed += (sender, args) => Notify(sender, args, ChangeType.Renamed);
    }

    public void Dispose()
    {

    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        return OnChange(callback, state);
    }
    public IDisposable OnChange<T>(Action<FileSystemChangeArgs<T>> callback, T state)
    {
        ThrowHelper.ThrowIfNull(callback);

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Changed,
            State = state,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnCreate<T>(Action<FileSystemChangeArgs<T>> callback, T state)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Created,
            State = state,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnDelete<T>(Action<FileSystemChangeArgs<T>> callback, T state)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Deleted,
            State = state,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnRename<T>(Action<FileSystemRenameArgs<T>> callback, T state)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new RenameSubscriber<T>()
        {
            ChangeType = ChangeType.Renamed,
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
        Renamed
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
        public required Action<FileSystemChangeArgs<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            Callback.Invoke(new FileSystemChangeArgs<T>(args.FullPath, State));
        }
    }

    partial class RenameSubscriber<T> : Subscriber
    {
        public required T State { get; init; }
        public required Action<FileSystemRenameArgs<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            var renameArgs = (RenamedEventArgs)args;

            Callback.Invoke(new FileSystemRenameArgs<T>(renameArgs.OldFullPath, renameArgs.FullPath, State));
        }
    }
}
