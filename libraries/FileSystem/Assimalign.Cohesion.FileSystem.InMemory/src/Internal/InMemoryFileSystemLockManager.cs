using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class InMemoryFileSystemLockManager : IDisposable
{
    private readonly Stack<InMemoryFileSystemLockHandle> _handles;

    public InMemoryFileSystemLockManager()
    {
        _handles = new Stack<InMemoryFileSystemLockHandle>();
    }

    public void Lock(InMemoryFileSystemLockHandle handle, LockPolicy policy)
    {
        handle.Lock(policy);
        _handles.Push(handle);
    }

    public void Dispose()
    {
        while (_handles.Count > 0)
        {
            var handle = _handles.Pop();
            handle.Unlock();
        }
    }
}
