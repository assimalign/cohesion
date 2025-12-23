using Assimalign.Cohesion.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

public class IsolatedFileSystem : IFileSystem
{
    private readonly IsolatedStorageFile _storage;

    public IsolatedFileSystem()
    {
        _storage = IsolatedStorageFile.GetStore(IsolatedStorageScope.User, null);
    }

    public Size Size => _storage.Quota;
    public Size SpaceAvailable => _storage.AvailableFreeSpace;
    public Size SpaceUsed => _storage.UsedSize;

    public string Name => throw new NotImplementedException();

    public bool IsReadOnly => throw new NotImplementedException();

    public IFileSystemDirectory RootDirectory => throw new NotImplementedException();

    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(CreateDirectory));

        _storage.CreateDirectory


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
        _storage.get
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

    public IFileSystemEventToken Watch(Glob? pattern)
    {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void CheckIfReadOnly(string? operation = null)
    {
        if (IsReadOnly)
        {
            ThrowHelper.ThrowInvalidOperationException($"The operation {operation} is not allowed. FileSystem is read-only.");
        }
    }
}
