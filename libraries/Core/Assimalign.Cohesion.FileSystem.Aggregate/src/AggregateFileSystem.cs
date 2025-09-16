using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

public sealed class AggregateFileSystem : IFileSystem
{
    public Size Size => throw new NotImplementedException();

    public Size SpaceAvailable => throw new NotImplementedException();

    public Size SpaceUsed => throw new NotImplementedException();

    public string Name => throw new NotImplementedException();

    public bool IsReadOnly => throw new NotImplementedException();

    public IFileSystemDirectory RootDirectory => throw new NotImplementedException();

    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public void DeleteDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public void DeleteFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        throw new NotImplementedException();
    }

    public bool Exists(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile GetFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        throw new NotImplementedException();
    }

    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemChangeToken Watch(Glob? pattern)
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}
