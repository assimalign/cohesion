using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class PhysicalFileSystemChangeToken : IFileSystemChangeToken
{
    private readonly FileSystemWatcher watcher;

    PhysicalFileSystemChangeToken()
    {
        
    }

    public PhysicalFileSystemChangeToken(string path) : this()
    {
        watcher = new FileSystemWatcher(path);
    }


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
