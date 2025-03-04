using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal abstract class InMemoryFileSystemInfo : IFileSystemInfo, IDisposable
{
    private int _count;
    private FileShare? _shared;

    public InMemoryFileSystemInfo()
    {
        var timestamp = DateTime.Now;

        CreatedOn = timestamp;
        UpdatedOn = timestamp;
        AccessedOn = timestamp;

        Tokens = new List<InMemoryFileSystemChangeToken>();
    }

    public bool IsLocked => _count != 0;
    public FileSystemPath Path => GetPath(this);
    public DateTime UpdatedOn { get; set; }
    public DateTime AccessedOn { get; set; }
    public DateTime CreatedOn { get; }
    public StringComparer Comparer { get; init; } = StringComparer.Ordinal;
    public InMemoryFileSystem FileSystem { get; init; } = default!;
    public List<InMemoryFileSystemChangeToken> Tokens { get; }
    protected InMemoryFileSystemChangeToken GetToken(InMemoryFileSystemInfo info)
    {
        var token = info switch
        {
            InMemoryFileSystemDirectory directory => new InMemoryFileSystemChangeToken(directory),
            InMemoryFileSystemFile file => new InMemoryFileSystemChangeToken(file)
        };

        Tokens.Add(token);

        return token;
    }

    public abstract void Dispose();

    private FileSystemPath GetPath(InMemoryFileSystemInfo info)
    {
        return info switch
        {
            InMemoryFileSystemDirectory directory => GetPath(directory),
            InMemoryFileSystemFile file => GetPath(file)
        };
    }
    private FileSystemPath GetPath(InMemoryFileSystemDirectory directory)
    {
        if (directory.HasParent(out var parent))
        {
            return FileSystemPath.Combine(GetPath((InMemoryFileSystemDirectory)parent), directory.Name);
        }

        return directory.Name;
    }
    private FileSystemPath GetPath(InMemoryFileSystemFile file)
    {
        return FileSystemPath.Combine(GetPath(file.Directory), file.Name);
    }


    #region Locking Mechanisms for FieSystem Nodes

    protected void BeginLock()
    {
        Monitor.Enter(this);
        try
        {
            while (_count != 0)
            {
                Monitor.Wait(this);
            }
            _count = -1;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    protected bool TryBeginLock()
    {
        Monitor.Enter(this);
        try
        {
            if (_count != 0)
            {
                return false;
            }
            _count = -1;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
        return true;
    }
    protected void Endlock()
    {
        Monitor.Enter(this);
        try
        {
            Debug.Assert(_count < 0);
            _count = 0;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    protected void BeginSharedLock(FileShare share)
    {
        Monitor.Enter(this);
        try
        {
            while (_count < 0)
            {
                Monitor.Wait(this);
            }
            if (_shared.HasValue)
            {
                var current = _shared.Value;
                // The previous share must be a superset of the shared being asked
                if ((share & current) != share)
                {
                    throw new UnauthorizedAccessException();// $"Cannot access shared resource path `{context}` with shared access`{share}` while current is `{currentShare}`");
                }
            }
            else
            {
                _shared = share;
            }
            _count++;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    protected bool TryBeginSharedLock(FileShare share)
    {
        Monitor.Enter(this);
        try
        {
            if (_count < 0)
            {
                return false;
            }
            if (_shared.HasValue)
            {
                var current = _shared.Value;

                if ((share & current) != share)
                {
                    return false;
                }
            }
            else
            {
                _shared = share;
            }
            _count++;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
        return true;
    }
    protected void EndSharedLock()
    {
        Monitor.Enter(this);
        try
        {
            Debug.Assert(_count > 0);
            _count--;
            if (_count == 0)
            {
                _shared = null;
            }
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }

    #endregion
}
