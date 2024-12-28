using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
namespace Assimalign.Cohesion.FileSystem;


using Internal;
using Globbing;

/// <summary>
/// Represents a directory on a physical filesystem
/// </summary>
[DebuggerDisplay("{Path}")]
internal class PhysicalFileSystemDirectory : IFileSystemDirectory
{
    private readonly string directory;
    private readonly DirectoryInfo directoryInfo;
    private readonly ExclusionFilterType directoryInfoFilters;

    private IEnumerable<IFileSystemInfo> files;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="directory"></param>
    public PhysicalFileSystemDirectory(string directory)
        : this(directory, ExclusionFilterType.Sensitive) { }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="directory"></param>
    /// <param name="filters"></param>
    public PhysicalFileSystemDirectory(string directory, ExclusionFilterType filters)
        : this(new DirectoryInfo(directory), filters)
    {
        this.directory = directory;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="directoryInfo"></param>
    public PhysicalFileSystemDirectory(DirectoryInfo directoryInfo)
        : this(directoryInfo, ExclusionFilterType.Sensitive) { }

    /// <summary>
    /// Initializes an instance of <see cref="PhysicalFileSystemDirectory"/> that wraps an instance of <see cref="System.IO.DirectoryInfo"/>
    /// </summary>
    /// <param name="info">The directory</param>
    internal PhysicalFileSystemDirectory(DirectoryInfo directoryInfo, ExclusionFilterType filters)
    {

        this.directoryInfo = directoryInfo;
    }


    private void EnsureInitialized()
    {
        try
        {
            files = directoryInfo
                .EnumerateFileSystemInfos()
                .Where(info => !FileSystemInfoHelper.IsExcluded(info, directoryInfoFilters))
                .Select<FileSystemInfo, IFileSystemInfo>(info =>
                {
                    return info switch
                    {
                        FileInfo file => new PhysicalFileSystemFile(file),
                        DirectoryInfo directory => new PhysicalFileSystemDirectory(directory),
                    };
                });
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is IOException)
        {
            files = Enumerable.Empty<IFileSystemInfo>();
        }
    }

    public IFileSystemDirectory GetDirectory(FileName name)
    {
        throw new NotImplementedException();
    }

    public bool TryGetDirectory(FileName name, out IFileSystemDirectory? directory)
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile GetFile(FileName name)
    {
        throw new NotImplementedException();
    }

    public bool TryGetFile(FileName name, out IFileSystemFile? file)
    {
        throw new NotImplementedException();
    }

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return this.OfType<IFileSystemDirectory>();
    }
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return this.OfType<IFileSystemFile>();
    }
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return directoryInfo.EnumerateFileSystemInfos("*", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true
        })
            .Select<FileSystemInfo, IFileSystemInfo>(item => item switch
            {
                FileInfo info => new PhysicalFileSystemFile(info),
                DirectoryInfo info => new PhysicalFileSystemDirectory(info),
                _ => throw new Exception("Invalid object in physical file system.")

            }).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Exist(Path path)
    {
        throw new NotImplementedException();
    }

    public IFileSystemDirectory GetDirectory(Path path)
    {
        throw new NotImplementedException();
    }

    public bool TryGetDirectory(Path path, out IFileSystemDirectory? directory)
    {
        throw new NotImplementedException();
    }

    public IFileSystemFile GetFile(Path path)
    {
        throw new NotImplementedException();
    }

    public bool TryGetFile(Path path, out IFileSystemFile? file)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    public bool Exists => directoryInfo.Exists;

    /// <summary>
    /// Always equals -1.
    /// </summary>
    public long Length => -1;

    /// <inheritdoc />
    public string PhysicalPath => directoryInfo.FullName;

    /// <inheritdoc />
    public string Name => directoryInfo.Name;

    /// <summary>
    /// The time when the directory was last written to.
    /// </summary>
    public DateTimeOffset LastModified => directoryInfo.LastWriteTimeUtc;

    /// <summary>
    /// Always true.
    /// </summary>
    public bool IsDirectory => true;

    /// <inheritdoc />
    public string FullName => directoryInfo.FullName;

    public long Count => throw new NotImplementedException();

    public IFileSystemDirectory Parent => throw new NotImplementedException();


    public DateTimeOffset UpdatedDateTime => throw new NotImplementedException();

    public DateTimeOffset CreatedDateTime => throw new NotImplementedException();

    Path IFileSystemInfo.Path => throw new NotImplementedException();
}