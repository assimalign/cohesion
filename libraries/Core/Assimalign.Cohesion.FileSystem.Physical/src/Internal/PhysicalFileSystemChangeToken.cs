using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class PhysicalFileSystemChangeToken : IFileSystemChangeToken
{
    public bool HasChanged => throw new NotImplementedException();
    public bool ActiveChangeCallbacks => throw new NotImplementedException();

    public void OnChange(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }

    public IDisposable OnChange(Action<object> callback, object state)
    {
        throw new NotImplementedException();
    }

    public IDisposable OnChange(Action callback)
    {
        throw new NotImplementedException();
    }

    public void OnDelete(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }

    public void OnRename(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }

    IDisposable IFileSystemChangeToken.OnChange(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }

    IDisposable IFileSystemChangeToken.OnDelete(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }

    IDisposable IFileSystemChangeToken.OnRename(Action<IFileSystemInfo> callback)
    {
        throw new NotImplementedException();
    }
}
