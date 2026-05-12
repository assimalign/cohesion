using System;
using System.IO;
using System.IO.IsolatedStorage;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal abstract class IsolatedFileSystemInfo : IFileSystemInfo
{
    private readonly IsolatedFileSystem _fileSystem;
    private readonly IsolatedStorageFile _storage;
    private readonly FileSystemPath _path;

    public IsolatedFileSystemInfo(
        IsolatedFileSystem fileSystem,
        IsolatedStorageFile storage,
        FileSystemPath path)
    {
        _fileSystem = fileSystem;
        _storage = storage;
        _path = path;
    }
    public FileSystemPath Path => _path;
    public DateTime CreatedOn => _storage.GetCreationTime(Path).DateTime;
    public DateTime UpdatedOn => _storage.GetLastWriteTime(Path).DateTime;
    public DateTime AccessedOn => _storage.GetLastAccessTime(Path).DateTime;
    public IsolatedFileSystem FileSystem => _fileSystem;
    IFileSystem IFileSystemInfo.FileSystem => FileSystem;
    public FileAttributes Attributes => throw new NotSupportedException();
    public void SetAttributes(FileAttributes attributes)
    {
        throw new NotSupportedException();
    }
}
