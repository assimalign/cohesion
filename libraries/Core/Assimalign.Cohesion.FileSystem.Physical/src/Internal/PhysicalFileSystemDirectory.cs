using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;


namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;


using Internal;

/// <summary>
/// Represents a directory on a physical filesystem
/// </summary>
[DebuggerDisplay("{Path}")]
internal class PhysicalFileSystemDirectory :  PhysicalFileSystemInfo, IFileSystemDirectory
{
    //private readonly string directory;
    //private readonly ExclusionFilterType directoryInfoFilters;

    private readonly DirectoryInfo _directoryInfo;

    public PhysicalFileSystemDirectory(DirectoryInfo directoryInfo) 
        : base(directoryInfo)
    {
        _directoryInfo = directoryInfo;
    }

    public DirectoryName Name => _directoryInfo.Name.Split(':')[0];
    public IFileSystemDirectory? Parent
    {
        get
        {
            if (_directoryInfo.Parent is not null)
            {
                return new PhysicalFileSystemDirectory(_directoryInfo.Parent);
            }

            return null;
        }
    }
    public IFileSystemChangeToken Watch(string filter)
    {
        return new PhysicalFileSystemChangeToken(this, filter);
    }
    public bool Exist(FileSystemPath path)
    {
        var fullPath = GetFullPath(path);

#if NET7_0_OR_GREATER
        return System.IO.Path.Exists(fullPath);
#else
        return File.Exists(fullPath) || Directory.Exists(fullPath);
#endif
    }

    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        var directoryPath = GetFullPath(path);
        var directoryInfo = new DirectoryInfo(directoryPath);

        CheckFileOrDirectoryExist(directoryInfo);

        return new PhysicalFileSystemDirectory(directoryInfo)
        {
            FileSystem = base.FileSystem,
            IgnoreAttributes = base.IgnoreAttributes
        };
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return _directoryInfo.EnumerateFiles("*", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = base.IgnoreAttributes
        })
            .Select(fileInfo => new PhysicalFileSystemFile(fileInfo)
            {
                FileSystem = base.FileSystem
            });
    }
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return _directoryInfo.EnumerateDirectories("*", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = base.IgnoreAttributes
        })
            .Select(directoryInfo => new PhysicalFileSystemDirectory(directoryInfo)
            {
                FileSystem = base.FileSystem
            });
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        var filePath = GetFullPath(path);
        var fileInfo = new FileInfo(filePath);

        CheckFileOrDirectoryExist(fileInfo);

        return new PhysicalFileSystemFile(fileInfo)
        {
            FileSystem = base.FileSystem,
            IgnoreAttributes = base.IgnoreAttributes
        };
    }
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfReadOnly();

        var fullPath = GetFullPath(path);

        try
        {
            var directoryInfo = Directory.CreateDirectory(fullPath);

            return new PhysicalFileSystemDirectory(directoryInfo);
        }
        catch (Exception)
        {
            throw;
        }
    }
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        var filePath = GetFullPath(path);
        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Exists)
        {
            throw new IOException("File already exists");
        }

        using var fileStream = fileInfo.Create();

        return new PhysicalFileSystemFile(fileInfo);
    }
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfReadOnly();

        var directoryPath = GetFullPath(path);
        var directoryInfo = new DirectoryInfo(directoryPath);

        CheckFileOrDirectoryExist(directoryInfo);

        directoryInfo.Delete();
    }
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfReadOnly();

        var fullPath = GetFullPath(path);
        var fileInfo = new FileInfo(fullPath);

        CheckFileOrDirectoryExist(fileInfo);

        fileInfo.Delete();
    }
    public void Copy(FileSystemPath source, FileSystemPath destination)
    {
        
        throw new NotImplementedException();


    }
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly();

        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            // TODO: throw difference exception
            throw new IOException("source not found");
        }
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return _directoryInfo.EnumerateFileSystemInfos("*", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            AttributesToSkip = base.IgnoreAttributes,
        })
            .Select<FileSystemInfo, IFileSystemInfo>(item => item switch
            {
                FileInfo info => new PhysicalFileSystemFile(info)
                {
                    FileSystem = base.FileSystem
                },
                DirectoryInfo info => new PhysicalFileSystemDirectory(info)
                {
                    FileSystem = base.FileSystem
                },
                _ => throw new Exception("Invalid object in physical file system.")

            }).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private FileSystemPath GetFullPath(FileSystemPath path)
    {
        return System.IO.Path.GetFullPath(path, _directoryInfo.FullName);
    }

    private void CheckIfReadOnly()
    {
        if (FileSystem.IsReadOnly)
        {
            ThrowHelper.ThrowFileSystemIsReadOnly();
        }
    }
    private void CheckFileOrDirectoryExist(FileSystemInfo fileSystemInfo)
    {
        if (!fileSystemInfo.Exists)
        {
            ThrowHelper.ThrowPathNotExistException(fileSystemInfo.FullName);
        }
    }
}