using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Globbing;

internal class PhysicalFileSystemChangeToken : IFileSystemChangeToken
{
    // TODO: Need to create a file system polling object. FileSystemWatcher is only available on Windows.
    private readonly FileSystemWatcher _watcher;
    private readonly GlobPatternMatcher _matcher;
    private readonly PhysicalFileSystemInfo _fileSystemInfo;
    private readonly List<Subscriber> _subscribers;

    public PhysicalFileSystemChangeToken(PhysicalFileSystemFile file, GlobPatternMatcher matcher)
    {
        _fileSystemInfo = file;
        _watcher = new FileSystemWatcher(file.Path);
        _subscribers = new List<Subscriber>();
        _matcher = matcher;
        Setup();
    }

    public PhysicalFileSystemChangeToken(PhysicalFileSystemDirectory directory, GlobPatternMatcher matcher)
    {
        _fileSystemInfo = directory;
        _watcher = new FileSystemWatcher(directory.Path);
        _subscribers = new List<Subscriber>();
        _matcher = matcher;
        Setup();
    }

    public IDisposable OnChange(Action<object> callback)
    {
        return OnChange(state => callback(state));
    }
    public IDisposable OnChange(Action<IFileSystemChangeContext> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber()
        {
            ChangeType = ChangeType.Changed,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnCreate(Action<IFileSystemChangeContext> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber()
        {
            ChangeType = ChangeType.Created,
            Callback = callback,
            OnDispose = Subscriber => _subscribers.Remove(Subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnDelete(Action<IFileSystemChangeContext> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber()
        {
            ChangeType = ChangeType.Deleted,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }

    private void Setup()
    {
        _watcher!.EnableRaisingEvents = true;
        _watcher.Created += (sender, args) => Notify(sender, args, ChangeType.Created);
        _watcher.Deleted += (sender, args) => Notify(sender, args, ChangeType.Deleted);
        _watcher.Changed += (sender, args) => Notify(sender, args, ChangeType.Changed);
    }
    private void Notify(object sender, FileSystemEventArgs args, ChangeType changeType)
    {
        FileSystemPath path = args.FullPath;
        IFileSystem fileSystem = _fileSystemInfo.FileSystem;

        if (!fileSystem.TryGetInfo(path, out var info))
        {
            // TODO: decide what to do. This should not occure
        }



        if(_fileSystemInfo.Path == path)
        {

        }
        foreach (var subscriber in _subscribers)
        {

            if (subscriber.ChangeType == changeType)
            {
                subscriber.Invoke(new PhysicalFileSystemChangeContext()
                {
                    Path = args.FullPath,
                    Info = _fileSystemInfo
                });
            }
        }
    }
    enum ChangeType
    {
        Created,
        Deleted,
        Changed
    }
    partial class Subscriber : IDisposable
    {
        public ChangeType ChangeType { get; init; }
        public Action<Subscriber> OnDispose { get; init; } = default!;
        public Action<IFileSystemChangeContext> Callback { get; init; } = default!;
        public void Invoke(IFileSystemChangeContext info)
        {
            Callback.Invoke(info);
        }
        public void Dispose()
        {
            OnDispose.Invoke(this);
        }
    }
    partial class PhysicalFileSystemChangeContext : IFileSystemChangeContext
    {
        public FileSystemPath Path { get; init; }
        public IFileSystemInfo Info { get; init; } = default!;
    }
}