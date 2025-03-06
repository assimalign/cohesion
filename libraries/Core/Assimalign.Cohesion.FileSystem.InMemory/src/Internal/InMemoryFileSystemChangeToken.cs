using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Globbing;
using Assimalign.Cohesion.Internal;

internal class InMemoryFileSystemChangeToken : IFileSystemChangeToken
{
    private readonly FileSystemPath _path;
    private readonly List<Subscriber> _subscribers;
    private readonly FilePatternMatcher _matcher;

    public InMemoryFileSystemChangeToken(FileSystemPath path)
    {
        _path = path;
        _subscribers = new List<Subscriber>();
    }

    public void NofityChange(InMemoryFileSystemInfo info)
    {
        if (info is InMemoryFileSystemDirectory directory )
        {
            if (_matcher.Execute(directory).HasMatches)
            {

            }
        }
    }
    public void NofityCreate(InMemoryFileSystemInfo info)
    {
    }
    public void NotifyDelete(InMemoryFileSystemInfo info)
    {
    }

    public IDisposable OnChange(Action<object> callback)
    {
        return OnChange(info => callback(info));
    }

    public IDisposable OnChange(Action<IFileSystemChangeContext> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber()
        {
            ChangeType = ChangeType.Change,
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
            ChangeType = ChangeType.Create,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }

    public IDisposable OnDelete(Action<IFileSystemChangeContext> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new Subscriber()
        {
            ChangeType = ChangeType.Delete,
            Callback = callback,
            OnDispose = subscriber => _subscribers.Remove(subscriber)
        };

        _subscribers.Add(disposable);

        return disposable;
    }

    enum ChangeType
    {
        Change,
        Create,
        Delete
    }
    partial class Subscriber : IDisposable
    {
        public ChangeType ChangeType { get; init; }
        public Action<IFileSystemChangeContext> Callback { get; init; } = default!;
        public Action<Subscriber> OnDispose { get; init; } = default!;
        public void Dispose()
        {
            OnDispose.Invoke(this);
        }
    }
}
