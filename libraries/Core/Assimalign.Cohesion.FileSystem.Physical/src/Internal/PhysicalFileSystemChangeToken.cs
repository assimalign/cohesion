using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class PhysicalFileSystemChangeToken : IFileSystemChangeToken
{
    private readonly FileSystemWatcher _watcher;

    public PhysicalFileSystemChangeToken(PhysicalFileSystemFile file, string? filters = null) : this()
    {
        _watcher = new FileSystemWatcher(file.Path);
    }

    public PhysicalFileSystemChangeToken(PhysicalFileSystemDirectory directory, string? filters = null) : this()
    {
        _watcher = new FileSystemWatcher(directory.Path);
    }

    private PhysicalFileSystemChangeToken()
    {
        _watcher!.EnableRaisingEvents = true;
        _watcher!.Created += (sender, args) =>
        {
            OnChangeSubscribers.ForEach(subscriber => subscriber.Action.Invoke(default!));
        };
        _watcher!.Deleted += (sender, args) =>
        {
            foreach (var subscriber in OnDeleteSubscribers)
            {
                subscriber.Action.Invoke(default!);
            }
        };
        _watcher!.Changed += (sender, args) =>
        {
            foreach (var subscriber in OnChangeSubscribers)
            {
                subscriber.Action.Invoke(default!);
            }
        };
    }


    public List<OnChangeSubscriber> OnChangeSubscribers { get; } = new List<OnChangeSubscriber>();
    public List<OnDeleteSubscriber> OnDeleteSubscribers { get; } = new List<OnDeleteSubscriber>();
    public List<OnCreateSubscriber> OnCreateSubscribers { get; } = new List<OnCreateSubscriber>();

    public IDisposable OnChange(Action<object> callback)
    {
        return OnChange(state => callback(state));
    }

    public IDisposable OnChange(Action<IFileSystemInfo> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new OnChangeSubscriber(this)
        {
            Action = callback
        };

        OnChangeSubscribers.Add(disposable);

        return disposable;
    }
    public IDisposable OnCreate(Action<IFileSystemInfo> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new OnCreateSubscriber(this)
        {
            Action = callback
        };

        OnCreateSubscribers.Add(disposable);

        return disposable;
    }

    public IDisposable OnDelete(Action<IFileSystemInfo> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        var disposable = new OnDeleteSubscriber(this)
        {
            Action = callback
        };

        OnDeleteSubscribers.Add(disposable);

        return disposable;
    }

    public partial class OnChangeSubscriber : IDisposable
    {
        private readonly PhysicalFileSystemChangeToken _token;

        public OnChangeSubscriber(PhysicalFileSystemChangeToken token)
        {
            _token = token;
        }

        public Action<IFileSystemInfo> Action { get; init; } = default!;

        public void Dispose()
        {
            _token.OnChangeSubscribers.Remove(this);
        }
    }

    public partial class OnDeleteSubscriber : IDisposable
    {
        private readonly PhysicalFileSystemChangeToken _token;

        public OnDeleteSubscriber(PhysicalFileSystemChangeToken token)
        {
            _token = token;
        }

        public Action<IFileSystemInfo> Action { get; init; } = default!;

        public void Dispose()
        {
            _token.OnDeleteSubscribers.Remove(this);
        }
    }
    public partial class OnCreateSubscriber : IDisposable
    {
        private readonly PhysicalFileSystemChangeToken _token;

        public OnCreateSubscriber(PhysicalFileSystemChangeToken token)
        {
            _token = token;
        }

        public Action<IFileSystemInfo> Action { get; init; } = default!;

        public void Dispose()
        {
            _token.OnCreateSubscribers.Remove(this);
        }
    }
}
