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

/// <summary>
/// An <see cref="IFileSystem"/> over a directory on the host's disk.
/// </summary>
/// <remarks>
/// Every member that takes a path resolves it to a full host path and refuses it, with
/// <see cref="FileSystemErrorCode.PathOutsideRoot"/> and before touching the disk, unless the result
/// is the root or lies under the root on a segment boundary. Containment is lexical: it is decided on
/// the normalized path text that the disk APIs receive, and the operating system follows any link
/// on that path (see the package's <c>docs/DESIGN.md</c>, "Root containment").
/// </remarks>
[DebuggerDisplay("Size: {Size} | Used: {SpaceUsed}")]
public class PhysicalFileSystem : IFileSystem
{
    // Windows and the Apple platforms default to case-insensitive file systems and every other host
    // to case-sensitive ones, the same split the BCL uses for its own path comparisons.
    private static readonly StringComparison _pathComparison =
        OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOS()
        || OperatingSystem.IsIOS()
        || OperatingSystem.IsTvOS()
        || OperatingSystem.IsWatchOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly DriveInfo _driveInfo;
    private readonly PhysicalFileSystemDirectory _root;
    private readonly string _rootFullPath;
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

        // Captured once, already normalized by DirectoryInfo, so later changes to the process's
        // current directory cannot move the root that containment is checked against.
        _rootFullPath = Path.TrimEndingDirectorySeparator(rootDirectory.FullName);
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

    /// <summary>
    /// Checks whether a file or directory exists at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">A path relative to the root, or a fully qualified host path under it.</param>
    /// <returns><see langword="true"/> when a file or directory exists at the resolved path.</returns>
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public bool Exists(FileSystemPath path) => Path.Exists(ResolvePath(path));

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public IFileSystemDirectory CreateDirectory(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(CreateDirectory));

        DirectoryInfo info = default!;

        try
        {
            string fullPath = ResolvePath(path);

            info = new DirectoryInfo(fullPath);

            if (info.Exists)
            {
                FileSystemException.ThrowPathAlreadyExist(path);
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

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public IFileSystemFile CreateFile(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(CreateFile));

        FileInfo info = default!;

        try
        {
            string fullPath = ResolvePath(path);

            info = new FileInfo(fullPath);

            if (info.Exists)
            {
                FileSystemException.ThrowPathAlreadyExist(path);
            }

            // FileInfo.Create does not auto-create the parent directory chain. Mirror the
            // behavior of the InMemory provider so callers can create nested paths in one step.
            if (info.Directory is { Exists: false } parent)
            {
                parent.Create();
            }

            info.Create().Dispose();
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
            FileSystemException.ThrowPathAlreadyExist(path, exception);
        }

        return new PhysicalFileSystemFile(this, info);
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public IFileSystemInfo GetInfo(FileSystemPath path)
    {
        IFileSystemInfo? info = default;

        try
        {
            string fullPath = ResolvePath(path);
            FileSystemInfo? fileSystemInfo = default;

            if ((fileSystemInfo = new DirectoryInfo(fullPath)).Exists)
            {
                 info = new PhysicalFileSystemDirectory(this, (DirectoryInfo)fileSystemInfo);
            }

            if ((fileSystemInfo = new FileInfo(fullPath)).Exists)
            {
                info = new PhysicalFileSystemFile(this, (FileInfo)fileSystemInfo);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(path, exception);
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
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            FileSystemException.ThrowPathAlreadyExist(path, exception);
        }

        if (info is null)
        {
            FileSystemException.ThrowPathNotFound(path);
        }

        return info!;
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public void DeleteDirectory(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(DeleteDirectory));

        try
        {
            string fullPath = ResolvePath(path);

            var info = new DirectoryInfo(fullPath);

            if (!info.Exists)
            {
                FileSystemException.ThrowDirectoryNotFound(path);
            }

            info.Delete(true);
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(path, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowDirectoryNotFound(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            FileSystemException.ThrowPathAlreadyExist(path, exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public void DeleteFile(FileSystemPath path)
    {
        CheckIfReadOnly(nameof(DeleteFile));

        try
        {
            string fullPath = ResolvePath(path);

            var info = new FileInfo(fullPath);

            if (!info.Exists)
            {
                FileSystemException.ThrowFileNotFound(path);
            }

            info.Delete();
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(path, exception);
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
            FileSystemException.ThrowAccessDenied(path, exception);
        }
        catch (IOException exception) when (exception.HResult == -2147024816)
        {
            FileSystemException.ThrowPathAlreadyExist(path, exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public IFileSystemDirectory GetDirectory(FileSystemPath path)
    {
        return (PhysicalFileSystemDirectory)GetInfo(path);
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="path"/> resolves outside the root (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public IFileSystemFile GetFile(FileSystemPath path)
    {
        return (PhysicalFileSystemFile)GetInfo(path);
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="source"/> or <paramref name="destination"/> resolves outside the root
    /// (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public void CopyFile(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly(nameof(CopyFile));

        try
        {
            // Both ends are resolved before either is touched, so a refused destination leaves the
            // source unread and a refused source leaves the destination uncreated.
            string sourceFullPath = ResolvePath(source);
            string destinationFullPath = ResolvePath(destination);

            if (!File.Exists(sourceFullPath))
            {
                FileSystemException.ThrowFileNotFound(source);
            }

            // File.Copy on Linux throws raw IOException (with no HResult that maps cleanly to
            // our existing catch handlers) when the destination exists. Match the InMemory
            // contract by pre-checking and raising the unified Conflict exception.
            if (File.Exists(destinationFullPath) || Directory.Exists(destinationFullPath))
            {
                FileSystemException.ThrowPathAlreadyExist(destination);
            }

            // Mirror the InMemory provider and auto-create the destination's parent chain.
            var destinationInfo = new FileInfo(destinationFullPath);
            if (destinationInfo.Directory is { Exists: false } parent)
            {
                parent.Create();
            }

            File.Copy(sourceFullPath, destinationFullPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(source, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(source, exception);
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
            FileSystemException.ThrowPathAlreadyExist(source, exception);
        }
    }

    /// <inheritdoc />
    /// <exception cref="FileSystemException">
    /// <paramref name="source"/> or <paramref name="destination"/> resolves outside the root
    /// (<see cref="FileSystemErrorCode.PathOutsideRoot"/>).
    /// </exception>
    public void Move(FileSystemPath source, FileSystemPath destination)
    {
        CheckIfReadOnly(nameof(Move));

        try
        {
            string sourceFullPath = ResolvePath(source);
            string destinationFullPath = ResolvePath(destination);

            if (File.Exists(sourceFullPath))
            {
                File.Move(sourceFullPath, destinationFullPath);
            }
            else if (Directory.Exists(sourceFullPath))
            {
                Directory.Move(sourceFullPath, destinationFullPath);
            }
            else
            {
                FileSystemException.ThrowPathNotFound(source);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            FileSystemException.ThrowAccessDenied(source, exception);
        }
        catch (PathTooLongException exception)
        {
            FileSystemException.ThrowPathTooLong(source, exception);
        }
        catch (FileNotFoundException exception)
        {
            FileSystemException.ThrowFileNotFound(source, exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            FileSystemException.ThrowPathNotFound(source, exception);
        }
        catch (IOException exception) when(exception.HResult == -2147024864) // Win32 Code 32 - The process cannot access the file because it is being used by another process.
        {
            FileSystemException.ThrowAccessDenied(source, exception);
        }
        catch (IOException exception) when(exception.HResult == -2147024816)
        {
            FileSystemException.ThrowPathAlreadyExist(source, exception);
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

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="fullName"/> names this file system's root
    /// directory, compared with the platform's case sensitivity.
    /// </summary>
    internal bool IsRootDirectory(string fullName)
    {
        return Path.TrimEndingDirectorySeparator(fullName.AsSpan()).Equals(_rootFullPath, _pathComparison);
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the full host path that every <c>System.IO</c> call
    /// receives, or throws <see cref="FileSystemErrorCode.PathOutsideRoot"/> unless that path is the
    /// root or lies under it on a segment boundary. Nothing touches the disk before this returns.
    /// </summary>
    private string ResolvePath(FileSystemPath path)
    {
        string value = path.ToString();

        if (string.IsNullOrEmpty(value))
        {
            return _rootFullPath;
        }

        string candidate;

        if (path.HasRoot())
        {
            // A rooted path names a host location. Only a fully qualified one resolves without
            // process state: on Windows "C:file" and "/file" resolve against the current directory
            // or the current drive, so they cannot be shown to lie under the root.
            if (!Path.IsPathFullyQualified(value))
            {
                FileSystemException.ThrowPathOutsideRoot(path);
            }

            candidate = value;
        }
        else
        {
            candidate = Path.Join(_rootFullPath, value);
        }

        // GetFullPath applies the host's own normalization, so "." and ".." segments collapse here
        // and never reach the operating system, and on Windows trailing dots and spaces are trimmed
        // and reserved device names map to \\.\ paths. The check runs on exactly what is opened.
        string fullPath = Path.GetFullPath(candidate);
        ReadOnlySpan<char> normalized = Path.TrimEndingDirectorySeparator(fullPath.AsSpan());

        if (!IsUnderRoot(normalized))
        {
            FileSystemException.ThrowPathOutsideRoot(path);
        }

        // Rebuild from the root's own text: a case-insensitive match must never hand the disk a
        // differently cased root, which on a case-sensitive volume would be another directory.
        return string.Concat(_rootFullPath.AsSpan(), normalized[_rootFullPath.Length..]);
    }

    private bool IsUnderRoot(ReadOnlySpan<char> fullPath)
    {
        ReadOnlySpan<char> root = _rootFullPath;

        if (!fullPath.StartsWith(root, _pathComparison))
        {
            return false;
        }

        // A volume root ("C:\", "/") already ends in a separator; any other root must be followed
        // by one, so "/srv/public2" does not lie under "/srv/public".
        return fullPath.Length == root.Length
            || Path.EndsInDirectorySeparator(root)
            || fullPath[root.Length] == Path.DirectorySeparatorChar
            || fullPath[root.Length] == Path.AltDirectorySeparatorChar;
    }

    private void CheckIfReadOnly(string? operation = null)
    {
        if (IsReadOnly)
        {
            FileSystemException.ThrowReadOnly(operation ?? string.Empty);
        }
    }
}
