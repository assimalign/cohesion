using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace Assimalign.Cohesion.FileSystem;

using Internal;

[DebuggerDisplay("{Name} - {Size}")]
public sealed class PhysicalFileSystem : IFileSystem
{
    private readonly DriveInfo driveInfo;
    private static readonly char[] separators =
    [
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar
    ];

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
        var fullPath = GetFullPath(path);
        var directoryInfo = Directory.CreateDirectory(fullPath);

        return new PhysicalFileSystemDirectory(directoryInfo);
    }
    public IFileSystemFile CreateFile(Path path)
    {
        var fullPath = GetFullPath(path);
        var stream = File.Create(fullPath);

        throw new NotImplementedException();
    }
    public void DeleteDirectory(Path path)
    {
        var fullPath = GetFullPath(path);

        CheckFileOrDirectoryExist(fullPath);

        Directory.Delete(fullPath);
    }
    public void DeleteFile(Path path)
    {
        var fullPath = GetFullPath(path);

        CheckFileOrDirectoryExist(fullPath);

        File.Delete(fullPath);
    }
    public IFileSystemDirectory GetDirectory(Path path)
    {
        CheckFileOrDirectoryExist(path);

        return new PhysicalFileSystemDirectory(path);
    }
    public IFileSystemFile GetFile(Path path)
    {
        CheckFileOrDirectoryExist(path);

        throw new NotImplementedException();
    }
    public void CopyFile(Path source, Path destination)
    {
        CheckFileOrDirectoryExist(source);

        File.Copy(source, destination);
    }
    public IFileSystemChangeToken Watch(string filter)
    {
        if (filter is null)
        {
            throw new ArgumentNullException(nameof(filter));
        }
        if (PathUtilities.HasInvalidFilterChars(filter))
        {
            throw new ArgumentException("The provider filter has an invalid character.");
        }

        // Relative paths starting with leading slashes are okay
        filter = filter.TrimStart(separators);
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
        return driveInfo.RootDirectory.EnumerateFileSystemInfos("*", new EnumerationOptions()
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

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    private Path GetFullPath(Path path)
    {
        return System.IO.Path.GetFullPath(path);
    }
    private void CheckFileOrDirectoryExist(Path path)
    {
        if (!Exist(path))
        {
            throw new FileSystemException($"The given path: {path} does not exist.");
        }
    }
    //private IEnumerable<TFileSystemInfo> Enumerate<TFileSystemInfo>(IEnumerable<IFileSystemInfo> enumerable)
    //    where TFileSystemInfo : IFileSystemInfo
    //{
    //    foreach (var item in enumerable)
    //    {
    //        if (item is TFileSystemInfo fsInfo)
    //        {
    //            yield return fsInfo;

    //            if (fsInfo is IEnumerable<IFileSystemInfo> children)
    //            {
    //                foreach (var child in Enumerate<TFileSystemInfo>(children))
    //                {
    //                    yield return child;
    //                }
    //            }
    //        }
    //    }
    //}
}
