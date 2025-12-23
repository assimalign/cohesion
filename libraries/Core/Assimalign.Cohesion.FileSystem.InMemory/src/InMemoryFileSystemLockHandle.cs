
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;

public abstract class InMemoryFileSystemLockHandle
{
    // _count  < 0 => This is an exclusive lock (_count == -1)
    // _count == 0 => No lock
    // _count  > 0 => This is a shared lock
    private int _count;
    private LockPolicy _policy;

    internal InMemoryFileSystemLockHandle() { }

    /// <summary>
    /// Specifies whether the current item is locked.
    /// </summary>
    public bool IsLocked => _count != 0;

    /// <summary>
    /// Checks whether the current item can be read.
    /// </summary>
    public bool CanRead => !_policy.HasFlag(LockPolicy.Read);

    /// <summary>
    /// Checks whether the current item can be written to.
    /// </summary>
    public bool CanWrite => !_policy.HasFlag(LockPolicy.Write);

    /// <summary>
    /// Checks whether the current item can be deleted.
    /// </summary>
    public bool CanDelete => !_policy.HasFlag(LockPolicy.Delete);

    internal void Lock(LockPolicy policy, bool recurse = false)
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
    internal void Lock(LockPolicy policy)
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
    internal void Unlock(bool recurse = false)
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
    internal void Unlock()
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
}
