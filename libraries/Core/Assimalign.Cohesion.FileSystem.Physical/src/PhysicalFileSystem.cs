using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Security.Principal;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;


[DebuggerDisplay("{Name} - Size: {Size} | Used: {SpaceUsed}")]
public class PhysicalFileSystem : IFileSystem, IReadOnlyFileSystem
{
    private static readonly char[] separators =
    [
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar
    ];

    private readonly DriveInfo driveInfo;
    private readonly bool isReadOnly;

    public PhysicalFileSystem(string drive)
    {
        this.driveInfo = new DriveInfo(drive);
    }

    public string Name => driveInfo.Name;
    public Size Size => driveInfo.TotalSize;
    public Size Space => driveInfo.TotalFreeSpace;
    public Size SpaceUsed => (driveInfo.TotalSize - driveInfo.TotalFreeSpace);
    public IFileSystemDirectory RootDirectory => new PhysicalFileSystemDirectory(driveInfo.RootDirectory);
    public bool Exist(FileSystemPath path)
    {
        var fullPath = GetFullPath(path);

#if NET7_0_OR_GREATER
        return System.IO.Path.Exists(fullPath);
#else
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return true;
        }
        return false;
#endif
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
        CheckIfReadOnly();

        var fullPath = GetFullPath(path);

        try
        {
            File.Create(fullPath).Close();

            var fileInfo = new FileInfo(path);

            return new PhysicalFileSystemFile(fileInfo);
        }
        catch (Exception)
        {
            throw;
        }
    }
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfReadOnly();

        var fullPath = GetFullPath(path);

        CheckFileOrDirectoryExist(fullPath);

        try
        {
            Directory.Delete(fullPath, true);
        }
        catch (Exception)
        {
            throw;
        }
    }
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfReadOnly();

        var fullPath = GetFullPath(path);

        CheckFileOrDirectoryExist(fullPath);

        try
        {
            File.Delete(fullPath);
        }
        catch (Exception)
        {
            throw;
        }
    }
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        CheckFileOrDirectoryExist(path);

        return new PhysicalFileSystemDirectory(path);
    }
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        CheckFileOrDirectoryExist(path);

        throw new NotImplementedException();
    }
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly();
        CheckFileOrDirectoryExist(source);

        File.Copy(source, destination);
    }
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly();

        try
        {
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
            else if (Directory.Exists(source))
            {
                Directory.Move(source, destination);
            }
        }
        catch (Exception)
        {
            throw;
        }
    }
    public IFileSystemChangeToken Watch(string filter)
    {
        if (filter is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(filter));
        }
        if (PathUtilities.HasInvalidFilterChars(filter))
        {
            ThrowHelper.ThrowArgumentException("The provider filter has an invalid character.");
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
            RecurseSubdirectories = true,
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

    private FileSystemPath GetFullPath(FileSystemPath path)
    {
        return Path.GetFullPath(path, driveInfo.Name);
    }
    private void CheckIfReadOnly()
    {
        if (isReadOnly)
        {
            ThrowHelper.ThrowFileSystemIsReadOnly();
        }
    }
    private void CheckFileOrDirectoryExist(FileSystemPath path)
    {
        if (!Exist(path))
        {
            ThrowHelper.ThrowFileNotExistException(path);
        }
    }


    public static IReadOnlyFileSystem CreateAsReadOnly()
    {
        return default!;
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
