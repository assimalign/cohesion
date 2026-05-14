using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystemEventToken"/> implementation that polls the backing
/// <see cref="IsolatedStorageFile"/> on a configurable interval and diffs successive snapshots
/// to surface <see cref="OnCreate"/>, <see cref="OnDelete"/> and <see cref="OnChange"/> events.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsolatedStorageFile"/> does not expose native change notifications. The polling
/// approach trades latency (changes are observed on the next tick, not in real time) for the
/// ability to surface a uniform event surface across providers.
/// </para>
/// <para>
/// Rename detection is not attempted — a rename surfaces as a <see cref="OnDelete"/> on the old
/// path followed by an <see cref="OnCreate"/> on the new path within the same tick.
/// <see cref="OnRename"/> registrations are accepted but never fired.
/// </para>
/// <para>
/// The token uses a <see cref="Timer"/> and an <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// guard to ensure polls do not re-enter even if a single tick takes longer than the configured
/// interval. After <see cref="Dispose"/> the timer is stopped and subsequent registrations and
/// callbacks are no-ops.
/// </para>
/// </remarks>
internal sealed class IsolatedFileSystemPollingEventToken : IFileSystemEventToken, IDisposable
{
    private readonly IsolatedStorageFile _storage;
    private readonly FileSystemPath _anchor;
    private readonly bool _anchorIsFile;
    private readonly Glob? _glob;
    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = new();
    private readonly Timer _timer;
    private Dictionary<string, EntrySnapshot> _snapshot;
    private int _polling;
    private bool _disposed;

    private IsolatedFileSystemPollingEventToken(
        IsolatedStorageFile storage,
        FileSystemPath anchor,
        bool anchorIsFile,
        Glob? glob,
        TimeSpan interval)
    {
        _storage = storage;
        _anchor = anchor;
        _anchorIsFile = anchorIsFile;
        _glob = glob;
        _snapshot = TakeSnapshot();
        _timer = new Timer(_ => Poll(), state: null, dueTime: interval, period: interval);
    }

    /// <summary>
    /// Watches the directory tree rooted at <paramref name="directoryAnchor"/> with an optional
    /// <paramref name="glob"/> filter applied to each emitted event.
    /// </summary>
    public static IsolatedFileSystemPollingEventToken ForDirectory(
        IsolatedStorageFile storage,
        FileSystemPath directoryAnchor,
        Glob? glob,
        TimeSpan interval)
        => new(storage, directoryAnchor, anchorIsFile: false, glob, interval);

    /// <summary>
    /// Watches a single file at <paramref name="fileAnchor"/>. Events fire only when the file
    /// itself changes (or is deleted); no other entries are observed.
    /// </summary>
    public static IsolatedFileSystemPollingEventToken ForFile(
        IsolatedStorageFile storage,
        FileSystemPath fileAnchor,
        TimeSpan interval)
        => new(storage, fileAnchor, anchorIsFile: true, glob: null, interval);

    /// <inheritdoc />
    public IDisposable OnChange(Action<object?> callback, object? state)
        => Register(ChangeType.Changed, state, args => callback(args.State), isRename: false);

    /// <inheritdoc />
    public IDisposable OnChange<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register<T>(ChangeType.Changed, state, callback);

    /// <inheritdoc />
    public IDisposable OnCreate<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register<T>(ChangeType.Created, state, callback);

    /// <inheritdoc />
    public IDisposable OnDelete<T>(Action<FileSystemEvent<T?>> callback, T? state)
        => Register<T>(ChangeType.Deleted, state, callback);

    /// <inheritdoc />
    /// <remarks>
    /// Polling cannot reliably correlate the delete + create pair that constitutes a rename, so
    /// the registration succeeds but the callback is never invoked. Subscribers that need rename
    /// fidelity should observe paired <see cref="OnDelete"/> + <see cref="OnCreate"/> events.
    /// </remarks>
    public IDisposable OnRename<T>(Action<FileSystemRenameEvent<T?>> callback, T? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // Registration accepted for parity with the other providers, but rename is not surfaced
        // through polling — see the remarks on the interface comment above.
        return new NoopRegistration();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();

        lock (_gate)
        {
            _subscribers.Clear();
        }
    }

    private IDisposable Register<T>(ChangeType changeType, T? state, Action<FileSystemEvent<T?>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_disposed)
        {
            return new NoopRegistration();
        }

        var subscriber = new TypedSubscriber<T>(
            changeType,
            state,
            callback,
            sub =>
            {
                lock (_gate)
                {
                    _subscribers.Remove(sub);
                }
            });

        lock (_gate)
        {
            _subscribers.Add(subscriber);
        }

        return subscriber;
    }

    private IDisposable Register(
        ChangeType changeType,
        object? state,
        Action<FileSystemEvent<object?>> callback,
        bool isRename)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (_disposed)
        {
            return new NoopRegistration();
        }

        return Register<object>(changeType, state, callback);
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        // Skip the tick if a previous poll is still running. Using a CAS guard avoids the
        // overhead of a Monitor when polls are nominally non-overlapping.
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Dictionary<string, EntrySnapshot> previous = _snapshot;
            Dictionary<string, EntrySnapshot> current = TakeSnapshot();

            foreach (var (path, snap) in current)
            {
                if (!previous.TryGetValue(path, out var prevSnap))
                {
                    Dispatch(ChangeType.Created, IsolatedPathHelper.FromStorePath(path));
                }
                else if (snap.Length != prevSnap.Length || snap.LastWriteUtc != prevSnap.LastWriteUtc)
                {
                    Dispatch(ChangeType.Changed, IsolatedPathHelper.FromStorePath(path));
                }
            }

            foreach (var (path, _) in previous)
            {
                if (!current.ContainsKey(path))
                {
                    Dispatch(ChangeType.Deleted, IsolatedPathHelper.FromStorePath(path));
                }
            }

            _snapshot = current;
        }
        catch
        {
            // Polling must never throw out of the timer callback. Swallow transient errors
            // (a dispose race, a file deletion mid-scan) and retry on the next tick.
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void Dispatch(ChangeType changeType, FileSystemPath path)
    {
        if (_glob is not null && !_glob.IsMatch(path))
        {
            return;
        }

        Subscriber[] copy;
        lock (_gate)
        {
            if (_subscribers.Count == 0)
            {
                return;
            }
            copy = _subscribers.ToArray();
        }

        foreach (var subscriber in copy)
        {
            if (subscriber.ChangeType == changeType)
            {
                try
                {
                    subscriber.Invoke(path, changeType);
                }
                catch
                {
                    // Subscriber callbacks must not crash the polling loop.
                }
            }
        }
    }

    private Dictionary<string, EntrySnapshot> TakeSnapshot()
    {
        var snapshot = new Dictionary<string, EntrySnapshot>(StringComparer.Ordinal);

        if (_anchorIsFile)
        {
            string store = IsolatedPathHelper.ToStorePath(_anchor);
            if (!string.IsNullOrEmpty(store) && _storage.FileExists(store))
            {
                snapshot[store] = ReadFileSnapshot(store);
            }
            return snapshot;
        }

        string root = IsolatedPathHelper.ToStorePath(_anchor);
        WalkDirectory(root, snapshot);
        return snapshot;
    }

    private void WalkDirectory(string storeDir, Dictionary<string, EntrySnapshot> sink)
    {
        string searchRoot = string.IsNullOrEmpty(storeDir) ? "*" : storeDir + "/*";

        try
        {
            foreach (var fileName in _storage.GetFileNames(searchRoot))
            {
                string filePath = string.IsNullOrEmpty(storeDir) ? fileName : storeDir + "/" + fileName;
                sink[filePath] = ReadFileSnapshot(filePath);
            }

            foreach (var dirName in _storage.GetDirectoryNames(searchRoot))
            {
                string dirPath = string.IsNullOrEmpty(storeDir) ? dirName : storeDir + "/" + dirName;
                WalkDirectory(dirPath, sink);
            }
        }
        catch
        {
            // Directory may have been removed mid-poll; treat as empty for this tick.
        }
    }

    private EntrySnapshot ReadFileSnapshot(string storePath)
    {
        long length = 0;
        DateTime lastWriteUtc = DateTime.MinValue;

        try
        {
            using var stream = _storage.OpenFile(storePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            length = stream.Length;
        }
        catch
        {
            // File may have been removed mid-walk; report 0 so the next snapshot diff will pick
            // it up as deleted.
        }

        try
        {
            lastWriteUtc = _storage.GetLastWriteTime(storePath).UtcDateTime;
        }
        catch
        {
            // ignored — fall back to default
        }

        return new EntrySnapshot(length, lastWriteUtc);
    }

    private readonly record struct EntrySnapshot(long Length, DateTime LastWriteUtc);

    private enum ChangeType { Created, Deleted, Changed }

    private abstract class Subscriber : IDisposable
    {
        protected Subscriber(ChangeType changeType, Action<Subscriber> onDispose)
        {
            ChangeType = changeType;
            OnDispose = onDispose;
        }

        public ChangeType ChangeType { get; }
        public Action<Subscriber> OnDispose { get; }
        public abstract void Invoke(FileSystemPath path, ChangeType changeType);
        public void Dispose() => OnDispose(this);
    }

    private sealed class TypedSubscriber<T> : Subscriber
    {
        private readonly T? _state;
        private readonly Action<FileSystemEvent<T?>> _callback;

        public TypedSubscriber(
            ChangeType changeType,
            T? state,
            Action<FileSystemEvent<T?>> callback,
            Action<Subscriber> onDispose)
            : base(changeType, onDispose)
        {
            _state = state;
            _callback = callback;
        }

        public override void Invoke(FileSystemPath path, ChangeType changeType)
            => _callback(new FileSystemEvent<T?>(path, _state, MapToFileSystemEventType(changeType)));

        private static FileSystemEventType MapToFileSystemEventType(ChangeType changeType) => changeType switch
        {
            ChangeType.Created => FileSystemEventType.Created,
            ChangeType.Deleted => FileSystemEventType.Deleted,
            ChangeType.Changed => FileSystemEventType.Changed,
            _ => FileSystemEventType.Changed,
        };
    }

    private sealed class NoopRegistration : IDisposable
    {
        public void Dispose() { }
    }
}
