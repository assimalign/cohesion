using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal class IsolatedFileSystemFile : IsolatedFileSystemInfo, IFileSystemFile
{
    private readonly Lazy<IsolatedStorageFileStream> _stream;
    public IsolatedFileSystemFile(IsolatedFileSystem fileSystem, IsolatedStorageFile storage, FileSystemPath path) 
        : base(fileSystem, storage, path)
    {
        _stream = new Lazy<IsolatedStorageFileStream>(() => new IsolatedStorageFileStream(path, FileMode.OpenOrCreate, storage));
    }

    public Size Size => throw new NotImplementedException();

    public FileName Name => Path.GetFileName()!.Value;

    public IFileSystemDirectory Directory => throw new NotImplementedException();

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public Stream Open()
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode)
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess)
    {
        throw new NotImplementedException();
    }

    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        throw new NotImplementedException();
    }

    public IFileSystemChangeToken Watch()
    {
        throw new NotImplementedException();
    }
}
