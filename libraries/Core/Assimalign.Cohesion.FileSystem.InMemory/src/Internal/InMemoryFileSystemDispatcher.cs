using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileSystemDispatcher : IDisposable
{
    private FileSystemEventHandler? _onChanged;
    private FileSystemEventHandler? _onCreated;
    private FileSystemEventHandler? _onDeleted;
    private RenamedEventHandler? _onRenamed;

    public InMemoryFileSystemDispatcher()
    {

    }

    public InMemoryFileSystemDispatcher(InMemoryFileSystemDispatcher parent)
    {
        Changed += parent._onChanged;
        Changed += parent._onDeleted;
        //Changed += parent._onRenamed;
    }


    public event FileSystemEventHandler? Changed
    {
        add => _onChanged = (FileSystemEventHandler)Delegate.Combine(_onChanged, value);
        remove => _onChanged = (FileSystemEventHandler)Delegate.Remove(_onChanged, value)!;
    }

    public event FileSystemEventHandler? Created
    {
        add => _onCreated = (FileSystemEventHandler)Delegate.Combine(_onCreated, value);
        remove => _onCreated = (FileSystemEventHandler)Delegate.Remove(_onCreated, value)!;
    }

    public event FileSystemEventHandler? Deleted
    {
        add => _onDeleted = (FileSystemEventHandler)Delegate.Combine(_onDeleted, value)!;
        remove => _onDeleted = (FileSystemEventHandler)Delegate.Remove(_onDeleted, value)!;
    }

    public event RenamedEventHandler? Renamed
    {
        add => _onRenamed = (RenamedEventHandler)Delegate.Combine(_onRenamed, value)!;
        remove => _onRenamed = (RenamedEventHandler)Delegate.Remove(_onRenamed, value)!;
    }

    public void RaiseEvent(FileSystemEventArgs args)
    {
        if (args.ChangeType == WatcherChangeTypes.Renamed)
        {
            _onRenamed?.Invoke(this, (RenamedEventArgs)args);
        }
        else
        {
            var handler = args.ChangeType switch
            {
                WatcherChangeTypes.Changed => _onChanged,
                WatcherChangeTypes.Created => _onCreated,
                WatcherChangeTypes.Deleted => _onDeleted,
                _ => throw new InvalidOperationException($"Unsupported change type '{args.ChangeType}'"),
            };

            handler?.Invoke(this, args);
        }
    }

    public void Dispose()
    {
        _onChanged = null!;
        _onCreated = null!;
        _onDeleted = null!;
        _onRenamed = null!;
    }
}
