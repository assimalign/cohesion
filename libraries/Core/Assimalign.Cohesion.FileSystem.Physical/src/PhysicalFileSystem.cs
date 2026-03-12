using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.Internal;
using Assimalign.Cohesion.FileSystem.Internal;
using System.Security;

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
    /// <param name="root"></param>
    public PhysicalFileSystem(FileSystemPath root) : this(new PhysicalFileSystemOptions() { Root = root })
    {

    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public PhysicalFileSystem(PhysicalFileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _driveInfo = new DriveInfo(options!.Root!);
        _isReadOnly = options.IsReadOnly;
        _name = options.Name ?? "PhysicalFileSystem";

        DirectoryInfo rootDirectory = _driveInfo.RootDirectory;

        if (options.Root != FileSystemPath.Parse( _driveInfo.Name))
        {
            rootDirectory = new DirectoryInfo(options.Root);
        }

        _root = new PhysicalFileSystemDirectory(this, rootDirectory)
        {
            IgnoreAttributes = options.IgnoreAttributes
        };
    }

    public string Name => _name;

    public bool IsReadOnly => _isReadOnly;

    public Size Size => _driveInfo.TotalSize;

    public Size SpaceAvailable => _driveInfo.TotalFreeSpace;

    public Size SpaceUsed => (_driveInfo.TotalSize - _driveInfo.TotalFreeSpace);

    public IFileSystemDirectory RootDirectory => _root;

    public bool Exists(FileSystemPath path) => Path.Exists(RootDirectory.Path.Merge(path));

    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(CreateDirectory));

        DirectoryInfo info = default!;

        try
        {
            // We don't need to check if path is within a valid scope of the root directory
            // The FileSystemPath type will handle that for us.
            FileSystemPath fullPath = RootDirectory.Path.Merge(path);

            info = new DirectoryInfo(fullPath);

            if (info.Exists)
            {
                ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path);
            }

            info.Create();
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            FileSystemException.ThrowDirectoryNotFound(path);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowDirectoryNotFound(path, exception);
        }

        return new PhysicalFileSystemDirectory(this, info);
    }

    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(CreateFile));

        FileInfo info = default!;

        try
        {
            // We don't need to check if path is within a valid scope of the root directory
            // The FileSystemPath type will handle that for us.
            FileSystemPath fullPath = RootDirectory.Path.Merge(path);

            info = new FileInfo(fullPath);

            if (info.Exists)
            {
                ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path);
            }

            info.Create().Dispose();
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (PathTooLongException exception)
        {
            ThrowHelper.ThrowPathTooLong(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path, exception);
        }

        return new PhysicalFileSystemFile(this, info);
    }

    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        IFileSystemInfo? info = default;

        try
        {
            // We don't need to check if path is within a valid scope of the root directory
            // The FileSystemPath type will handle that for us.
            FileSystemPath fileSystemPath = RootDirectory.Path.Merge(path);
            FileSystemInfo? fileSystemInfo = default;

            if ((fileSystemInfo = new DirectoryInfo(fileSystemPath)).Exists)
            {
                 info = new PhysicalFileSystemDirectory(this, (DirectoryInfo)fileSystemInfo);
            }

            if ((fileSystemInfo = new FileInfo(fileSystemPath)).Exists)
            {
                info = new PhysicalFileSystemFile(this, (FileInfo)fileSystemInfo);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (PathTooLongException exception)
        {
            ThrowHelper.ThrowPathTooLong(path, exception);
        }
        catch (FileNotFoundException exception)
        {
            FileSystemException.ThrowFileNotFound(path, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowDirectoryNotFound(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path, exception);
        }

        return info!;
    }

    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(DeleteDirectory));

        try
        {
            FileSystemPath fullPath = RootDirectory.Path.Merge(path);

            var info = new DirectoryInfo(fullPath);

            info.Delete(true);
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (PathTooLongException exception)
        {
            ThrowHelper.ThrowPathTooLong(path, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowDirectoryNotFound(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path, exception);
        }
    }

    public void DeleteFile(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(DeleteFile));

        try
        {
            FileSystemPath fullPath = RootDirectory.Path.Merge(path);

            var info = new FileInfo(fullPath);

            if (!info.Exists)
            {
                throw new InvalidOperationException("the File System directory does not exist.");
            }

            info.Delete();
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(path, exception);
        }
        catch (FileNotFoundException exception)
        {
            FileSystemException.ThrowFileNotFound(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            ThrowHelper.ThrowAccessNotAllowed(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(path, exception);
        }
    }

    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        return (PhysicalFileSystemDirectory)GetInfo(path);
    }

    public IFileSystemFile GetFile(FileSystemPath path)
    {
        return (PhysicalFileSystemFile)GetInfo(path);
    }

    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly(nameof(CopyFile));

        try
        {
            File.Copy(source, destination);
        }
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(source, exception);
        }
        catch (PathTooLongException exception)
        {
            ThrowHelper.ThrowPathTooLong(source, exception);
        }
        catch (FileNotFoundException exception)
        {
            FileSystemException.ThrowFileNotFound(source, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowDirectoryNotFound(source, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            FileSystemException.ThrowAccessDenied(source, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(source, exception);
        }
    }

    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly(nameof(Move));

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
        catch (UnauthorizedAccessException exception)
        {
            ThrowHelper.ThrowAccessNotAllowed(source, exception);
        }
        catch (PathTooLongException exception)
        {
            ThrowHelper.ThrowPathTooLong(source, exception);
        }
        catch (FileNotFoundException exception)
        {
            FileSystemException.ThrowFileNotFound(source, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            ThrowHelper.ThrowPathNotFound(source, exception);
        }
        catch (IOException exception) when(exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            ThrowHelper.ThrowAccessNotAllowed(source, exception);
        }
        catch (IOException exception) when(exception.HResult == -2147024816)
        {
            ThrowHelper.ThrowFileOrDirectoryAlreadyExists(source, exception);
        }
    }
    
    public IFileSystemEventToken Watch(Glob? pattern)
    {
        return RootDirectory.Watch(pattern);
    }
    
    public IEnumerable<IFileSystemInfo> EnumerateFileSystem(FileSystemEnumerationOptions? options = default)
    {
        options ??= new FileSystemEnumerationOptions()
        {
            AttributesToSkip = _root.IgnoreAttributes,
            Recurse = false
        };

        return RootDirectory.EnumerateFileSystem(options);
    }
    
    public IEnumerator<IFileSystemInfo> GetEnumerator()
    {
        return EnumerateFileSystem(new FileSystemEnumerationOptions()
        {
            Recurse = true,
            AttributesToSkip = _root.IgnoreAttributes

        }).GetEnumerator();
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

    private void CheckIfReadOnly(string? operation = null)
    {
        InvalidOperationException.ThrowIf(IsReadOnly, $"The operation {operation} is not allowed. FileSystem is read-only.");
    }
}
