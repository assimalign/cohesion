using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

internal abstract class PhysicalFileSystemInfo : IFileSystemInfo
{
    private readonly FileSystemInfo _fileSystemInfo;

    protected PhysicalFileSystemInfo(FileSystemInfo fileSystemInfo)
    {
        _fileSystemInfo = fileSystemInfo;
    }

    public FileSystemPath Path => _fileSystemInfo.FullName;
    public DateTime UpdatedOn => _fileSystemInfo.LastWriteTimeUtc;
    public DateTime CreatedOn => _fileSystemInfo.CreationTimeUtc;
    public DateTime AccessedOn => _fileSystemInfo.LastAccessTimeUtc;
    public FileAttributes IgnoreAttributes { get; init; }
    public PhysicalFileSystem FileSystem { get; init; } = default!;
    public FileAttributes Attributes { get; internal set; }
    public void SetAttributes(FileAttributes attributes)
    {
        Attributes = attributes;
    }
}
