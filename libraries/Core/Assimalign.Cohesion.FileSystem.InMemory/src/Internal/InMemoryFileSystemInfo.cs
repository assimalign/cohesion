using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

[Flags]
internal enum LockPolicy
{
    Unlocked = 0,
    Read = 1,
    Write = 2,
    Delete = 4,
    Exclusive = Read | Write | Delete
}


internal abstract class InMemoryFileSystemInfo : IFileSystemInfo, IDisposable
{
    // _count  < 0 => This is an exclusive lock (_count == -1)
    // _count == 0 => No lock
    // _count  > 0 => This is a shared lock
    private int _count;
    private LockPolicy _policy;

    private readonly InMemoryFileSystem _fileSystem;
    private readonly DateTime _createdOn;
    private DateTime _accessedOn;
    private DateTime _updatedOn;
    private FileAttributes _attributes;
    private InMemoryFileSystemDispatcher _dispatcher;

    protected InMemoryFileSystemInfo(InMemoryFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        _createdOn = DateTime.Now;
        _accessedOn = _createdOn;
        _updatedOn = _createdOn;
        _dispatcher = new InMemoryFileSystemDispatcher();
    }

    public bool IsLocked => _count != 0;
    public DateTime UpdatedOn => _updatedOn;
    public DateTime AccessedOn => _accessedOn;
    public DateTime CreatedOn => _createdOn;
    public FileSystemPath Path => GetPath(this);
    public FileAttributes Attributes => _attributes;
    public FileSystemPathComparer Comparer { get; init; } = FileSystemPathComparer.InvariantCulture;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public InMemoryFileSystemDispatcher Dispatcher => _dispatcher;
    public InMemoryFileSystem FileSystem => _fileSystem;
    IFileSystem IFileSystemInfo.FileSystem => FileSystem;
    public void SetAccessedOn(DateTime accessedOn) => _accessedOn = accessedOn;
    public void SetUpdatedOn(DateTime updatedOn) => _updatedOn = updatedOn;
    public void SetAttributes(FileAttributes attributes) => _attributes = attributes;
    public virtual void Dispose()
    {
        Dispatcher.Dispose();
    }
    private FileSystemPath GetPath(InMemoryFileSystemInfo info)
    {
        return info switch
        {
            InMemoryFileSystemDirectory directory => GetPath(directory),
            InMemoryFileSystemFile file => GetPath(file),
            _ => throw new Exception()
        };
    }
    private FileSystemPath GetPath(InMemoryFileSystemDirectory directory)
    {
        if (directory.HasParent(out var parent))
        {
            return FileSystemPath.Merge(GetPath((InMemoryFileSystemDirectory)parent), directory.Name);
        }

        return directory.Name;
    }
    private FileSystemPath GetPath(InMemoryFileSystemFile file)
    {
        return FileSystemPath.Merge(GetPath(file.Directory), file.Name);
    }
    private bool HasParent(out InMemoryFileSystemDirectory parent) => this.HasParent<InMemoryFileSystemDirectory>(out parent);

    #region FieSystem Locking Mechanisms

    protected void Lock(LockPolicy policy, bool recurse = false)
    {
        if (recurse && this is InMemoryFileSystemDirectory directory)
        {
            var successes = new List<InMemoryFileSystemInfo>(directory.Count);

            try
            {
                foreach (var child in directory.Cast<InMemoryFileSystemInfo>())
                {
                    child.Lock(policy, recurse);
                    successes.Add(child);
                }
            }
            catch
            {
                // Rollback all locks
                foreach (var child in successes)
                {
                    child.Unlock();
                }
                throw;
            }
        }

        Lock(policy);
    }
    protected void Lock(LockPolicy policy)
    {
        Monitor.Enter(this);

        try
        {
            //var source = new CancellationTokenSource(LockTimeout);

            //source.TryReset()
            if (policy.HasFlag(LockPolicy.Exclusive))
            {
                // Wait for all shared locks to be released
                while (_count != 0)
                {
                    Monitor.Wait(this);
                }
                _count = -1;
                _policy = LockPolicy.Exclusive;
                Monitor.PulseAll(this);
            }
            else
            {
                // Wait for all exclusive locks to be released
                while (_count < 0)
                {
                    Monitor.Wait(this);
                }
                // If not unlocked then check that the requested share is a subset of the current share
                if ((_policy & LockPolicy.Unlocked) != LockPolicy.Unlocked)
                {
                    if (!_policy.HasFlag(policy))
                    {
                        throw new UnauthorizedAccessException();// $"Cannot access shared resource path `{context}` with shared access`{share}` while current is `{currentShare}`");
                    }
                }
                else
                {
                    _policy = policy;
                }
                _count++;
                Monitor.PulseAll(this);
            }
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    protected void Unlock(bool recurse = false)
    {
        if (recurse && this is InMemoryFileSystemDirectory directory)
        {
            var successes = new List<InMemoryFileSystemInfo>(directory.Count);

            try
            {
                foreach (var child in directory.Cast<InMemoryFileSystemInfo>())
                {
                    child.Unlock(recurse);
                    successes.Add(child);
                }
            }
            catch
            {
                // Rollback all locks
                foreach (var child in successes)
                {
                    child.Unlock();
                }
                throw;
            }
        }
    }
    protected void Unlock()
    {
        Monitor.Enter(this);
        try
        {
            if (_policy.HasFlag(LockPolicy.Exclusive))
            {
                Debug.Assert(_count < 0);
                _count = 0;
                _policy = LockPolicy.Unlocked;
                Monitor.PulseAll(this);
            }
            else
            {
                Debug.Assert(_count > 0, "A shared lock was never called.");
                _count--;
                if (_count == 0)
                {
                    _policy = LockPolicy.Unlocked;
                }
                Monitor.PulseAll(this);
            }
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    //private bool TryBeginLock()
    //{
    //    Monitor.Enter(this);
    //    try
    //    {
    //        if (_lockCount != 0)
    //        {
    //            return false;
    //        }
    //        _lockCount = -1;
    //        Monitor.PulseAll(this);
    //    }
    //    finally
    //    {
    //        Monitor.Exit(this);
    //    }
    //    return true;
    //}
    //private bool TryBeginSharedLock(LockKind share)
    //{
    //    // Begin locking all parents from root first
    //    if (this.HasParent<InMemoryFileSystemDirectory>(out var parent))
    //    {
    //        // If parent fails to lock and 
    //        if (!parent.TryBeginSharedLock(share))
    //        {

    //        }
    //    }

    //    Monitor.Enter(this);

    //    try
    //    {
    //        if (_lockCount < 0)
    //        {
    //            return false;
    //        }
    //        if (_lockKind.HasValue)
    //        {
    //            var current = _lockKind.Value;

    //            if ((share & current) != share)
    //            {
    //                return false;
    //            }
    //        }
    //        else
    //        {
    //            _lockKind = share;
    //        }
    //        _lockCount++;
    //        Monitor.PulseAll(this);
    //    }
    //    finally
    //    {
    //        Monitor.Exit(this);
    //    }
    //    return true;
    //}

    #endregion
}
