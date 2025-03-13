using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Represents a directory on a physical filesystem
/// </summary>
[DebuggerDisplay("[D] - {Path}")]
internal class PhysicalFileSystemDirectory : PhysicalFileSystemInfo, IFileSystemDirectory
{
    private readonly DirectoryInfo _directoryInfo;

    public PhysicalFileSystemDirectory(PhysicalFileSystem fileSystem, DirectoryInfo directoryInfo)
        : base(fileSystem, directoryInfo)
    {
        _directoryInfo = directoryInfo;
    }

    public DirectoryName Name => _directoryInfo.Name;
    public IFileSystemDirectory? Parent
    {
        get
        {
            if (_directoryInfo.Parent is not null)
            {
                return new PhysicalFileSystemDirectory(FileSystem, _directoryInfo.Parent)
                {
                    IgnoreAttributes = IgnoreAttributes
                };
            }

            return null;
        }
    }
    public IFileSystemChangeToken Watch(Glob? pattern)
    {
        pattern ??= Glob.Parse(Path);

        return new PhysicalFileSystemChangeToken(
            this,
            pattern);
    }
    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(Path.Combine(name));

            CheckFileOrDirectoryExist(directoryInfo);

            return new PhysicalFileSystemDirectory(FileSystem, directoryInfo)
            {
                IgnoreAttributes = IgnoreAttributes
            };
        }
        catch (Exception exception) when (exception is not FileSystemException)
        {
            throw new FileSystemException("", exception);
        }
    }
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return _directoryInfo.EnumerateFiles("*", GetEnumerationOptions())
            .Select(fileInfo => new PhysicalFileSystemFile(FileSystem, fileInfo)
            {
                IgnoreAttributes = IgnoreAttributes
            });
    }
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return _directoryInfo.EnumerateDirectories("*", GetEnumerationOptions())
            .Select(directoryInfo => new PhysicalFileSystemDirectory(FileSystem, directoryInfo)
            {
                IgnoreAttributes = IgnoreAttributes
            });
    }
    public IFileSystemFile GetFile(FileName name)
    {
        try
        {
            var info = new FileInfo(Path.Combine(name));

            CheckFileOrDirectoryExist(info);

            return new PhysicalFileSystemFile(FileSystem, info)
            {
                IgnoreAttributes = IgnoreAttributes
            };
        }
        catch (Exception exception)
        when (exception is not FileSystemException)
        {
            throw new FileSystemException("", exception);
        }
    }
    public IFileSystemDirectory CreateDirectory(DirectoryName name)
    {
        CheckIfReadOnly();

        try
        {
            var path = Path.Combine(name);

            return new PhysicalFileSystemDirectory(
                FileSystem,
                Directory.CreateDirectory(path))
            {
                IgnoreAttributes = IgnoreAttributes
            };
        }
        catch (Exception)
        {
            throw;
        }
    }
    public IFileSystemFile CreateFile(FileName name)
    {
        var filePath = GetFullPath(name);
        var fileInfo = new FileInfo(filePath);

        if (fileInfo.Exists)
        {
            throw new IOException("File already exists");
        }

        using var fileStream = fileInfo.Create();

        return new PhysicalFileSystemFile(FileSystem, fileInfo)
        {
            IgnoreAttributes = IgnoreAttributes
        };
    }

    public void DeleteDirectory(DirectoryName name)
    {
        CheckIfReadOnly();

        var directoryPath = GetFullPath(name);
        var directoryInfo = new DirectoryInfo(directoryPath);

        CheckFileOrDirectoryExist(directoryInfo);

        directoryInfo.Delete();
    }

    public void DeleteFile(FileName name)
    {
        try
        {
            CheckIfReadOnly();

            var path = Path.Combine(name);
            var info = new FileInfo(path);

            CheckFileOrDirectoryExist(info);

            info.Delete();
        }
        catch (IOException exception)
        {

        }
        catch (SecurityException excpetion)
        {
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowFileSystemException("", exception);
        }
    }


    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return _directoryInfo.EnumerateFileSystemInfos("*", GetEnumerationOptions())
            .Select<FileSystemInfo, IFileSystemInfo>(item => item switch
            {
                FileInfo info => new PhysicalFileSystemFile(FileSystem, info)
                {
                    IgnoreAttributes = IgnoreAttributes
                },
                DirectoryInfo info => new PhysicalFileSystemDirectory(FileSystem, info)
                {
                    IgnoreAttributes = IgnoreAttributes
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
        // Check if the file or directory has the ignore attribute
        if (fileSystemInfo.Attributes.HasFlag(base.IgnoreAttributes))
        {
            ThrowHelper.ThrowPathNotExistException(fileSystemInfo.FullName);
        }
    }
    private EnumerationOptions GetEnumerationOptions()
    {
        return new EnumerationOptions()
        {
            IgnoreInaccessible = true,

            AttributesToSkip = base.IgnoreAttributes
        };
    }
}