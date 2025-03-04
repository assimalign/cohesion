using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class InMemoryFileSystemChangeToken : IFileSystemChangeToken, IDisposable
{
    private readonly InMemoryFileSystemInfo _fileSystemInfo;

    public InMemoryFileSystemChangeToken(InMemoryFileSystemFile file)
    {
        _fileSystemInfo = file;
    }

    public InMemoryFileSystemChangeToken(InMemoryFileSystemDirectory directory)
    {
        _fileSystemInfo = directory;
    }

    public List<OnChangeSubscriber> OnChangeSubscribers { get; } = new List<OnChangeSubscriber>();
    public List<OnCreateSubscriber> OnCreateSubscribers { get; } = new List<OnCreateSubscriber>();
    public List<OnDeleteSubscriber> OnDeleteSubscribers { get; } = new List<OnDeleteSubscriber>();
    public void Dispose()
    {
        _fileSystemInfo.Tokens.Remove(this);
    }

    public IDisposable OnChange(Action<object> callback)
    {
        return OnChange(info => callback(info));
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

        throw new NotImplementedException();
    }

    public IDisposable OnDelete(Action<IFileSystemInfo> callback)
    {
        ThrowHelper.ThrowIfNull(callback, nameof(callback));

        throw new NotImplementedException();
    }

    public partial class OnChangeSubscriber : IDisposable
    {
        private readonly InMemoryFileSystemChangeToken _token;

        public OnChangeSubscriber(InMemoryFileSystemChangeToken token)
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
        private readonly InMemoryFileSystemChangeToken _token;

        public OnDeleteSubscriber(InMemoryFileSystemChangeToken token)
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
        private readonly InMemoryFileSystemChangeToken _token;

        public OnCreateSubscriber(InMemoryFileSystemChangeToken token)
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
