using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal abstract class PhysicalFileSystemInfo : IFileSystemInfo
{
    private readonly FileSystemInfo _fileSystemInfo;
    private readonly PhysicalFileSystem _fileSystem;

    protected PhysicalFileSystemInfo(
        PhysicalFileSystem fileSystem,
        FileSystemInfo fileSystemInfo)
    {
        _fileSystem = fileSystem;
        _fileSystemInfo = fileSystemInfo;
    }

    public FileSystemPath Path => _fileSystemInfo.FullName;
    public DateTime UpdatedOn => _fileSystemInfo.LastWriteTimeUtc;
    public DateTime CreatedOn => _fileSystemInfo.CreationTimeUtc;
    public DateTime AccessedOn => _fileSystemInfo.LastAccessTimeUtc;
    public PhysicalFileSystem FileSystem => _fileSystem;
    IFileSystem IFileSystemInfo.FileSystem => FileSystem;
    public FileAttributes Attributes => _fileSystemInfo.Attributes;
    public FileAttributes IgnoreAttributes { get; init; }
    public void SetAttributes(FileAttributes attributes)
    {
        try
        {
            _fileSystemInfo.Attributes = attributes;
        }
        catch (Exception exception) 
        when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            FileSystemException.ThrowNotFound(Path, exception);
        }
    }

    public EnumerationOptions GetEnumerationOptions(bool recurse = false)
    {
        return new EnumerationOptions()
        {
            RecurseSubdirectories = recurse,
            AttributesToSkip = IgnoreAttributes,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = true
        };
    }
}
