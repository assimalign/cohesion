using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileSystemDispatcher : IDisposable
{
    private readonly InMemoryFileSystemDispatcher? _parent;
    private int _disposed;

    public InMemoryFileSystemDispatcher() { }

    public InMemoryFileSystemDispatcher(InMemoryFileSystemDispatcher parent)
    {
        _parent = parent;
    }

    public event FileSystemEventHandler? Changed;
    public event FileSystemEventHandler? Created;
    public event FileSystemEventHandler? Deleted;
    public event RenamedEventHandler? Renamed;

    public void RaiseEvent(FileSystemEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Forward to the live parent rather than copying its delegates into every child.
        // This makes later registration and unsubscription apply throughout the tree.
        _parent?.RaiseEvent(args);
        if (args.ChangeType == WatcherChangeTypes.Renamed)
        {
            Renamed?.Invoke(this, (RenamedEventArgs)args);
        }
        else
        {
            var handler = args.ChangeType switch
            {
                WatcherChangeTypes.Changed => Changed,
                WatcherChangeTypes.Created => Created,
                WatcherChangeTypes.Deleted => Deleted,
                _ => throw new InvalidOperationException($"Unsupported change type '{args.ChangeType}'"),
            };

            handler?.Invoke(this, args);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Changed = null;
        Created = null;
        Deleted = null;
        Renamed = null;
    }
}
