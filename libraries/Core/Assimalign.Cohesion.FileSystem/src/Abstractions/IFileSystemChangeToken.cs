using System;

namespace Assimalign.Cohesion.FileSystem;

public interface IFileSystemChangeToken : IChangeToken
{
    IDisposable OnChange(Action<IFileSystemInfo> callback);
    IDisposable OnDelete(Action<IFileSystemInfo> callback);
    IDisposable OnRename(Action<IFileSystemInfo> callback);
}
