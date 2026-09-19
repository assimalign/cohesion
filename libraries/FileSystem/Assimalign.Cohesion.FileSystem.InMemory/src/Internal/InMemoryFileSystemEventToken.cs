using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class InMemoryFileSystemEventToken : IFileSystemEventToken, IDisposable
{
    private readonly Glob _glob;
    private readonly List<Subscriber> _subscribers;
    private readonly object _gate = new();
    private readonly InMemoryFileSystemDispatcher _dispatcher;
    private readonly Action<InMemoryFileSystemEventToken> _onDispose;
    private int _disposed;

    public InMemoryFileSystemEventToken(InMemoryFileSystemInfo fileSystemInfo, Glob glob, Action<InMemoryFileSystemEventToken> onDispose)
    {
        _dispatcher = fileSystemInfo.Dispatcher;
        _onDispose = onDispose;
        _glob = glob;
        _subscribers = new List<Subscriber>();
        _dispatcher.Created += OnCreated;
        _dispatcher.Deleted += OnDeleted;
        _dispatcher.Changed += OnChanged;
        _dispatcher.Renamed += OnRenamed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispatcher.Created -= OnCreated;
        _dispatcher.Deleted -= OnDeleted;
        _dispatcher.Changed -= OnChanged;
        _dispatcher.Renamed -= OnRenamed;
        lock (_gate)
        {
            _subscribers.Clear();
        }
        _onDispose(this);
    }

    public IDisposable OnChange(Action<object?> callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<object?>()
        {
            ChangeType = ChangeType.Changed,
            State = state,
            Callback = args => callback(args.State),
            OnDispose = RemoveSubscriber
        };

        AddSubscriber(disposable);

        return disposable;
    }
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Changed,
            State = state,
            Callback = callback,
            OnDispose = RemoveSubscriber
        };

        AddSubscriber(disposable);

        return disposable;
    }
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Created,
            State = state,
            Callback = callback,
            OnDispose = RemoveSubscriber
        };

        AddSubscriber(disposable);

        return disposable;
    }
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new Subscriber<T>()
        {
            ChangeType = ChangeType.Deleted,
            State = state,
            Callback = callback,
            OnDispose = RemoveSubscriber
        };

        AddSubscriber(disposable);

        return disposable;
    }
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var disposable = new RenameSubscriber<T?>()
        {
            ChangeType = ChangeType.Renamed,
            State = state,
            Callback = callback,
            OnDispose = RemoveSubscriber
        };

        AddSubscriber(disposable);

        return disposable;
    }

    private void OnCreated(object? sender, FileSystemEventArgs args) => Notify(args, ChangeType.Created);
    private void OnDeleted(object? sender, FileSystemEventArgs args) => Notify(args, ChangeType.Deleted);
    private void OnChanged(object? sender, FileSystemEventArgs args) => Notify(args, ChangeType.Changed);
    private void OnRenamed(object? sender, RenamedEventArgs args) => Notify(args, ChangeType.Renamed);

    private void AddSubscriber(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                _subscribers.Add(subscriber);
            }
        }
    }

    private void RemoveSubscriber(Subscriber subscriber)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscriber);
        }
    }

    private void Notify(FileSystemEventArgs args, ChangeType changeType)
    {
        FileSystemPath fileSystemPath = args.FullPath;

        if (!_glob.IsMatch(fileSystemPath))
        {
            return;
        }

        Subscriber[] subscribers;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            subscribers = _subscribers.ToArray();
        }

        // Callers can unregister or dispose this token inside a callback. Never enumerate
        // the mutable list or hold its lock across user code.
        foreach (var subscriber in subscribers)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            if (!subscriber.IsDisposed && subscriber.ChangeType == changeType)
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
        private int _disposed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public required ChangeType ChangeType { get; init; }
        public Action<Subscriber> OnDispose { get; init; } = default!;
        public abstract void Invoke(FileSystemEventArgs args);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                OnDispose.Invoke(this);
            }
        }
    }

    partial class Subscriber<T> : Subscriber
    {
        public required T? State { get; init; }
        public required Action<FileSystemEvent<T?>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            Callback.Invoke(new FileSystemEvent<T?>(args.FullPath, State, (FileSystemEventType)args.ChangeType));
        }
    }

    partial class RenameSubscriber<T> : Subscriber
    {
        public required T State { get; init; }
        public required Action<FileSystemRenameEvent<T>> Callback { get; init; } = default!;
        public override void Invoke(FileSystemEventArgs args)
        {
            var renameArgs = (RenamedEventArgs)args;

            Callback.Invoke(new FileSystemRenameEvent<T>(renameArgs.OldFullPath, renameArgs.FullPath, State, (FileSystemEventType)args.ChangeType));
        }
    }
}
