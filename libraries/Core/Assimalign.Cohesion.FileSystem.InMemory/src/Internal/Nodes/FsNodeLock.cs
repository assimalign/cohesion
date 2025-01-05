using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class FsNodeLock
{
    private int count;
    private FileShare? shared;

    public FsNodeLock()
    {
        
    }

    public bool IsLocked => count != 0;
    public void EnterShared(Path context)
    {
        EnterShared(FileShare.Read, context);
    }
    public void EnterShared(FileShare share, Path context)
    {
        Monitor.Enter(this);
        try
        {
            while (count < 0)
            {
                Monitor.Wait(this);
            }
            if (shared.HasValue)
            {
                var currentShare = shared.Value;
                // The previous share must be a superset of the shared being asked
                if ((share & currentShare) != share)
                {
                    throw new UnauthorizedAccessException($"Cannot access shared resource path `{context}` with shared access`{share}` while current is `{currentShare}`");
                }
            }
            else
            {
                shared = share;
            }
            count++;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    public void ExitShared()
    {
        Monitor.Enter(this);
        try
        {
            Debug.Assert(count > 0);
            count--;
            if (count == 0)
            {
                shared = null;
            }
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    public bool TryEnterShared(FileShare share)
    {
        Monitor.Enter(this);
        try
        {
            if (count < 0)
            {
                return false;
            }
            if (shared.HasValue)
            {
                var currentShare = shared.Value;
                if ((share & currentShare) != share)
                {
                    return false;
                }
            }
            else
            {
                shared = share;
            }
            count++;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
        return true;
    }
    public bool TryEnterExclusive()
    {
        Monitor.Enter(this);
        try
        {
            if (count != 0)
            {
                return false;
            }
            count = -1;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
        return true;
    }
    public void EnterExclusive()
    {
        Monitor.Enter(this);
        try
        {
            while (count != 0)
            {
                Monitor.Wait(this);
            }
            count = -1;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    public void ExitExclusive()
    {
        Monitor.Enter(this);
        try
        {
            Debug.Assert(count < 0);
            count = 0;
            Monitor.PulseAll(this);
        }
        finally
        {
            Monitor.Exit(this);
        }
    }
    protected FsNodeLock Clone()
    {
        var locker = (FsNodeLock)MemberwiseClone();
        // Erase any locks
        locker.count = 0;
        locker.shared = null;
        return locker;
    }
}
