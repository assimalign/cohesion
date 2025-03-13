using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Principal;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;

[DebuggerDisplay("Size: {Size} | Used: {SpaceUsed}")]
public class PhysicalFileSystem : IFileSystem
{
    private readonly DriveInfo _driveInfo;
    private readonly PhysicalFileSystemDirectory _root;
    private bool _isReadOnly;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="root"></param>
    public PhysicalFileSystem(FileSystemPath root)
    {
        if (!Exists(root))
        {
            FileSystemException.ThrowNotFound(root);
        }
        _driveInfo = new DriveInfo(root!);
        _root = new PhysicalFileSystemDirectory(this, _driveInfo.RootDirectory)
        {
            IgnoreAttributes = PhysicalFileSystemOptions.Default.IgnoreAttributes
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public PhysicalFileSystem(PhysicalFileSystemOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        _driveInfo = new DriveInfo(options!.Root!);
        _root = new PhysicalFileSystemDirectory(this, _driveInfo.RootDirectory)
        {
            IgnoreAttributes = options.IgnoreAttributes
        };
    }
    public bool IsReadOnly => _isReadOnly;
    public Size Size => _driveInfo.TotalSize;
    public Size SpaceAvailable => _driveInfo.TotalFreeSpace;
    public Size SpaceUsed => (_driveInfo.TotalSize - _driveInfo.TotalFreeSpace);
    public IFileSystemDirectory RootDirectory => _root;
    public bool Exists(FileSystemPath path)
    {
#if NET7_0_OR_GREATER
        return System.IO.Path.Exists(path);
#else
        return File.Exists(path) || Directory.Exists(path);
#endif
    }

    public bool TryGetInfo(FileSystemPath path, out IFileSystemInfo? info)
    {
        info = null!;

        if (File.Exists(path))
        {
            info = new PhysicalFileSystemFile(this, new FileInfo(path)) 
            {
                IgnoreAttributes = _root.IgnoreAttributes
            };
        }
        if (Directory.Exists(path))
        {
            info = new PhysicalFileSystemDirectory(this, new DirectoryInfo(path))
            {
                IgnoreAttributes = _root.IgnoreAttributes
            };
        }

        return (info is not null);
    }

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
        //try
        //{
        //    var directoryInfo = _driveInfo.RootDirectory;
        //    var directoryPath = RootDirectory.Path.Combine(path);

        //    return new PhysicalFileSystemDirectory(
        //        directoryInfo.CreateSubdirectory(directoryPath));
        //}
        //catch (Exception exception) when (exception is not FileSystemException)
        //{
        //    throw new FileSystemException("", exception);
        //}
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
        //RootDirectory.DeleteFile(path);
    }
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        throw new NotImplementedException();
        //return RootDirectory.GetDirectory(path);
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        throw new NotImplementedException();
    }
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        throw new NotImplementedException();
    }
    

    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        //CheckIfReadOnly();

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
    public IFileSystemChangeToken Watch(Glob pattern)
    {
        return RootDirectory.Watch(pattern);
    }
    public IEnumerable<IFileSystemDirectory> EnumerateDirectories()
    {
        return RootDirectory.GetDirectories();
    }
    public IEnumerable<IFileSystemFile> EnumerateFiles()
    {
        return _driveInfo.RootDirectory
            .EnumerateFiles("*", _root.GetEnumerationOptions(true))
            .Select<FileSystemInfo, IFileSystemFile>(item => item switch
            {
                FileInfo info => new PhysicalFileSystemFile(this, info)
                {
                    IgnoreAttributes = _root.IgnoreAttributes
                },
                _ => throw new Exception("Invalid object in physical file system.")
            });
    }
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return RootDirectory.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Dispose()
    {

    }
    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
