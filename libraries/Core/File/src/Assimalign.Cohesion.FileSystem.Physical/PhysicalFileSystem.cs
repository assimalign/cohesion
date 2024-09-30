
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Internal;

public sealed class PhysicalFileSystem : IFileSystem
{
    private readonly DriveInfo driveInfo;

    public PhysicalFileSystem(string drive)
    {
        this.driveInfo = new DriveInfo(drive);
    }

    public string Name => driveInfo.Name;
    public Size Size => driveInfo.TotalSize;
    public Size Space => driveInfo.TotalFreeSpace;
    public Size SpaceUsed => (driveInfo.TotalSize - driveInfo.TotalFreeSpace);
    public IFileSystemDirectory RootDirectory => new PhysicalFileSystemDirectory(driveInfo.RootDirectory);

    public bool Exist(Path path)
    {
#if NET7_0_OR_GREATER

        return System.IO.Path.Exists(path);
#else
        var fullPath = System.IO.Path.GetFullPath(path);

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return true;
        }

        return false;
#endif
    }
    public IFileSystemDirectory CreateDirectory(Path path)
    {
        var directoryInfo = Directory.CreateDirectory(path);

        return new PhysicalFileSystemDirectory(directoryInfo);
    }

    public IFileSystemFile CreateFile(Path path)
    {
        var stream = File.Create(path);

        throw new NotImplementedException();
    }

    public void DeleteDirectory(Path path)
    {
        throw new NotImplementedException();
    }

    public void DeleteFile(Path path)
    {
        CheckFileOrDirectoryExist(path);
        File.Delete(path);
    }

    

    public IEnumerable<IFileSystemDirectory> GetDirectories()
    {
        return Enumerate<IFileSystemDirectory>(this);
    }
    public IEnumerable<IFileSystemFile> GetFiles()
    {
        return Enumerate<IFileSystemFile>(this);
    }
    public IFileSystemDirectory GetDirectory(Path path)
    {
        CheckFileOrDirectoryExist(path);



        throw new NotImplementedException();
    }

    

    public IFileSystemFile GetFile(Path path)
    {
        throw new NotImplementedException();
    }
    public void CopyFile(Path source, Path destination)
    {

        throw new NotImplementedException();
    }


    public IChangeToken Watch(string filter)
    {
        throw new NotImplementedException();
    }

    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return driveInfo.RootDirectory.EnumerateFileSystemInfos()
            .Select<FileSystemInfo, IFileSystemInfo>(item => item switch
            {
                FileInfo info => new PhysicalFileSystemFile(info),
                DirectoryInfo info => new PhysicalFileSystemDirectory(info),
                _ => throw new Exception("Invalid object in physical file system.")

            }).GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();


    private void CheckFileOrDirectoryExist(Path path)
    {
        if (!Exist(path))
        {
            throw new FileSystemException($"The given path: {path} does not exist.");
        }
    }

    private IEnumerable<TFileSystemInfo> Enumerate<TFileSystemInfo>(IEnumerable<IFileSystemInfo> enumerable)
        where TFileSystemInfo : IFileSystemInfo
    {
        foreach (var item in enumerable)
        {
            if (item is TFileSystemInfo fsInfo)
            {
                yield return fsInfo;

                if (fsInfo is IEnumerable<IFileSystemInfo> children)
                {
                    foreach (var child in Enumerate<TFileSystemInfo>(children))
                    {
                        yield return child;
                    }
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }
}
