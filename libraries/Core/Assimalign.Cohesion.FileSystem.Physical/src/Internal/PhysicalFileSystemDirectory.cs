using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem;

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

    public IFileSystemEventToken Watch(Glob? pattern)
    {
        return new PhysicalFileSystemChangeToken(
            this,
            pattern ?? Glob.Parse(Path));
    }

    public IFileSystemDirectory GetDirectory(DirectoryName name)
    {
        return FileSystem.GetDirectory(Path.Join(name));
    }

    public IFileSystemFile GetFile(FileName name)
    {
        return FileSystem.GetFile(Path.Join(name));
    }

    public IFileSystemDirectory CreateDirectory(DirectoryName name)
    {
        return FileSystem.CreateDirectory(Path.Join(name));
    }
    
    public IFileSystemFile CreateFile(FileName name)
    {
        return FileSystem.CreateFile(Path.Join(name));
    }

    public void DeleteDirectory(DirectoryName name)
    {
        FileSystem.DeleteDirectory(Path.Join(name));
    }

    public void DeleteFile(FileName name)
    {
        FileSystem.DeleteFile(Path.Join(name));
    }

    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return EnumerateFileSystem(new FileSystemEnumerationOptions()
        {
            Recurse = false

        }).OfType<IFileSystemFile>();
    }
    
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return EnumerateFileSystem(new FileSystemEnumerationOptions()
        {
            Recurse = false

        }).OfType<IFileSystemDirectory>();
    }

    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = null)
    {
        options ??= new FileSystemEnumerationOptions()
        {
            AttributesToSkip = IgnoreAttributes,
            Recurse = false
        };

        return _directoryInfo.EnumerateFileSystemInfos("*", new EnumerationOptions()
            {
                AttributesToSkip = options.AttributesToSkip,
                RecurseSubdirectories = options.Recurse,
            })
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
            });
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}