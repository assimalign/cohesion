using System;
using System.IO;
using System.Globalization;

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


internal abstract class InMemoryFileSystemInfo : InMemoryFileSystemLockHandle, IFileSystemInfo, IDisposable
{
    private readonly InMemoryFileSystem _fileSystem;
    private readonly DateTime _createdOn;
    private readonly CultureInfo _cultureInfo;
    private readonly bool _ignoreCase;
    private DateTime _accessedOn;
    private DateTime _updatedOn;
    private FileAttributes _attributes;
    private Lazy<InMemoryFileSystemDispatcher> _dispatcher;

    protected InMemoryFileSystemInfo(InMemoryFileSystem fileSystem, CultureInfo cultureInfo, bool ignoreCase)
    {
        _fileSystem = fileSystem;
        _createdOn = DateTime.Now;
        _accessedOn = _createdOn;
        _updatedOn = _createdOn;
        _cultureInfo = cultureInfo;
        _ignoreCase = ignoreCase;
        _dispatcher = new Lazy<InMemoryFileSystemDispatcher>(() => this switch
        {
            InMemoryFileSystemFile file => new InMemoryFileSystemDispatcher(file.Directory.Dispatcher),
            InMemoryFileSystemDirectory dir when dir.Parent is not null => new InMemoryFileSystemDispatcher(dir.Parent.Dispatcher),
            _ => new InMemoryFileSystemDispatcher()
        });
    }

    public DateTime UpdatedOn => _updatedOn;
    public DateTime AccessedOn => _accessedOn;
    public DateTime CreatedOn => _createdOn;
    public FileSystemPath Path => GetPath(this);
    public FileAttributes Attributes => _attributes;
    public FileAttributes IgnoreAttributes { get; init; }
    public bool IgnoreCase => _ignoreCase;
    public CultureInfo CultureInfo => _cultureInfo;
    public InMemoryFileSystem FileSystem => _fileSystem;
    public InMemoryFileSystemDispatcher Dispatcher => _dispatcher.Value;
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
        if (directory.HasParent<InMemoryFileSystemDirectory>(out var parent))
        {
            return FileSystemPath.Merge(GetPath((InMemoryFileSystemDirectory)parent), directory.Name);
        }

        return directory.Name;
    }
    private FileSystemPath GetPath(InMemoryFileSystemFile file)
    {
        return FileSystemPath.Merge(GetPath(file.Directory), file.Name);
    }
}


    //protected void Lock(LockPolicy policy, bool recurse = false)
    //{
    //    if (recurse && this is InMemoryFileSystemDirectory directory)
    //    {
    //        var successes = new List<InMemoryFileSystemInfo>(directory.Count);

    //        try
    //        {
    //            foreach (var child in directory.Cast<InMemoryFileSystemInfo>())
    //            {
    //                child.Lock(policy, recurse);
    //                successes.Add(child);
    //            }
    //        }
    //        catch
    //        {
    //            // Rollback all locks
    //            foreach (var child in successes)
    //            {
    //                child.Unlock();
    //            }
    //            throw;
    //        }
    //    }

    //    Lock(policy);
    //}
    //protected void Lock(LockPolicy policy)
    //{
    //    Monitor.Enter(this);

    //    try
    //    {
    //        //var source = new CancellationTokenSource(LockTimeout);

    //        //source.TryReset()
    //        if (policy.HasFlag(LockPolicy.Exclusive))
    //        {
    //            // Wait for all shared locks to be released
    //            while (_count != 0)
    //            {
    //                Monitor.Wait(this);
    //            }
    //            _count = -1;
    //            _policy = LockPolicy.Exclusive;
    //            Monitor.PulseAll(this);
    //        }
    //        else
    //        {
    //            // Wait for all exclusive locks to be released
    //            while (_count < 0)
    //            {
    //                Monitor.Wait(this);
    //            }
    //            // If not unlocked then check that the requested share is a subset of the current share
    //            if ((_policy & LockPolicy.Unlocked) != LockPolicy.Unlocked)
    //            {
    //                if (!_policy.HasFlag(policy))
    //                {
    //                    throw new UnauthorizedAccessException();// $"Cannot access shared resource path `{context}` with shared access`{share}` while current is `{currentShare}`");
    //                }
    //            }
    //            else
    //            {
    //                _policy = policy;
    //            }
    //            _count++;
    //            Monitor.PulseAll(this);
    //        }
    //    }
    //    finally
    //    {
    //        Monitor.Exit(this);
    //    }
    //}
    //protected void Unlock(bool recurse = false)
    //{
    //    if (recurse && this is InMemoryFileSystemDirectory directory)
    //    {
    //        var successes = new List<InMemoryFileSystemInfo>(directory.Count);

    //        try
    //        {
    //            foreach (var child in directory.Cast<InMemoryFileSystemInfo>())
    //            {
    //                child.Unlock(recurse);
    //                successes.Add(child);
    //            }
    //        }
    //        catch
    //        {
    //            // Rollback all locks
    //            foreach (var child in successes)
    //            {
    //                child.Unlock();
    //            }
    //            throw;
    //        }
    //    }
    //}
    //protected void Unlock()
    //{
    //    Monitor.Enter(this);
    //    try
    //    {
    //        if (_policy.HasFlag(LockPolicy.Exclusive))
    //        {
    //            Debug.Assert(_count < 0);
    //            _count = 0;
    //            _policy = LockPolicy.Unlocked;
    //            Monitor.PulseAll(this);
    //        }
    //        else
    //        {
    //            Debug.Assert(_count > 0, "A shared lock was never called.");
    //            _count--;
    //            if (_count == 0)
    //            {
    //                _policy = LockPolicy.Unlocked;
    //            }
    //            Monitor.PulseAll(this);
    //        }
    //    }
    //    finally
    //    {
    //        Monitor.Exit(this);
    //    }
    //}


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
