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
    private readonly string _name;
    private bool _isReadOnly;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public PhysicalFileSystem(PhysicalFileSystemOptions options)
    {
        ThrowHelper.ThrowIfNull(options, nameof(options));

        _driveInfo = new DriveInfo(options!.Drive!);
        _root = new PhysicalFileSystemDirectory(_driveInfo.RootDirectory)
        {
            FileSystem = this,
            IgnoreAttributes = options.IgnoreAttributes
        };
    }
    public bool IsReadOnly => _isReadOnly;
    public Size Size => _driveInfo.TotalSize;
    public Size SpaceAvailable => _driveInfo.TotalFreeSpace;
    public Size SpaceUsed => (_driveInfo.TotalSize - _driveInfo.TotalFreeSpace);
    public IFileSystemDirectory RootDirectory => _root;

    public bool Exist(FileSystemPath path)
    {
        return RootDirectory.Exist(path);
    }
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        return RootDirectory.CreateDirectory(path);
    }
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        return RootDirectory.CreateFile(path);
    }
    public void DeleteDirectory(FileSystemPath path)
    {
        RootDirectory.DeleteDirectory(path);
    }
    public void DeleteFile(FileSystemPath path)
    {
        RootDirectory.DeleteFile(path);
    }
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        return RootDirectory.GetDirectory(path);
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        return RootDirectory.GetFile(path);
    }
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        RootDirectory.CopyFile(source, destination);
    }
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        RootDirectory.Move(source, destination);
    }
    public IFileSystemChangeToken Watch(FileSystemPath pattern)
    {
        return new PhysicalFileSystemChangeToken(_root, pattern);
    }
    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return RootDirectory.GetDirectories();
    }
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return RootDirectory.GetFiles();
    }
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return RootDirectory.GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {

    }

    //public IReadOnlyFileSystem AsReadOnly()
    //{
    //    _isReadOnly = true;
    //    return this!;
    //}

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
