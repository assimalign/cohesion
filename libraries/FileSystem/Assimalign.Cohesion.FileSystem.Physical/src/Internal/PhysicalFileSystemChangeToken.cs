using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class PhysicalFileSystemChangeToken : IFileSystemEventToken, IDisposable
{
    private readonly object _sync = new();
    private readonly Glob _glob;
    private readonly FileSystemWatcher _watcher;
    private readonly List<Subscriber> _subscribers;
    private bool _disposed;

    public PhysicalFileSystemChangeToken(PhysicalFileSystemInfo fileSystemInfo, Glob glob)
    {
        _glob = glob;
        _subscribers = new List<Subscriber>();
        _watcher = new FileSystemWatcher(fileSystemInfo.Path);
        _watcher.IncludeSubdirectories = true;
        _watcher.Created += (sender, args) => Notify(sender, args, FileSystemEventType.Created);
        _watcher.Deleted += (sender, args) => Notify(sender, args, FileSystemEventType.Deleted);
        _watcher.Changed += (sender, args) => Notify(sender, args, FileSystemEventType.Changed);
        _watcher.Renamed += (sender, args) => Notify(sender, args, FileSystemEventType.Renamed);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscribers.Clear();
        }

        // Retire subscriptions before releasing the watcher. Already queued events can
        // still enter Notify, but they cannot dispatch any further subscriptions.
        _watcher.Dispose();
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<object?>()
        {
            ChangeType = FileSystemEventType.Changed,
            State = state,
            Callback = args => callback(args.State),
            OnDispose = Unsubscribe
        };

        return Subscribe(disposable);
    }
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T?>()
        {
            ChangeType = FileSystemEventType.Changed,
            State = state,
            Callback = callback,
            OnDispose = Unsubscribe
        };

        return Subscribe(disposable);
    }
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T?>()
        {
            ChangeType = FileSystemEventType.Created,
            State = state,
            Callback = callback,
            OnDispose = Unsubscribe
        };

        return Subscribe(disposable);
    }
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber<T?>()
        {
            ChangeType = FileSystemEventType.Deleted,
            State = state,
            Callback = callback,
            OnDispose = Unsubscribe
        };

        return Subscribe(disposable);
    }
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new RenameSubscriber<T>()
        {
            ChangeType = FileSystemEventType.Renamed,
            State = state!,
            Callback = (Action<FileSystemRenameEvent<T>>)(object)callback,
            OnDispose = Unsubscribe
        };

        return Subscribe(disposable);
    }

    private IDisposable Subscribe(Subscriber subscriber)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers.Add(subscriber);
            return subscriber;
        }
    }

    private void Unsubscribe(Subscriber subscriber)
    {
        lock (_sync)
        {
            _subscribers.Remove(subscriber);
        }
    }

    private void Notify(object? sender, FileSystemEventArgs args, FileSystemEventType changeType)
    {
        FileSystemPath fileSystemPath = args.FullPath;

        if (!_glob.IsMatch(fileSystemPath))
        {
            return;
        }

        Subscriber[] subscribers;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (subscriber.ChangeType != changeType || !_subscribers.Contains(subscriber))
                {
                    continue;
                }
            }

            // User callbacks run outside the gate so they can dispose this token or
            // their own registration. A callback already dispatched may finish later.
            subscriber.Invoke(args);
        }
    }
    abstract partial class Subscriber : IDisposable
    {
        public required FileSystemEventType ChangeType { get; init; }
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
            Callback.Invoke(new FileSystemEvent<T>(args.FullPath, State, ChangeType));
        }
    }

    partial class RenameSubscriber<T> : Subscriber
    {
        public required T State { get; init; }
        public required Action<FileSystemRenameEvent<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            var renameArgs = (RenamedEventArgs)args;

            Callback.Invoke(new FileSystemRenameEvent<T>(renameArgs.OldFullPath, renameArgs.FullPath, State, ChangeType));
        }
    }
}
