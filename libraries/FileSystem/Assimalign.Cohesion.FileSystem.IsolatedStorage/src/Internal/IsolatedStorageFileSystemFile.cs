using System;
using System.Diagnostics;
using System.IO;
using System.IO.IsolatedStorage;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// <see cref="IFileSystemFile"/> implementation backed by an entry inside an
/// <see cref="IsolatedStorageFile"/>. Each stream open delegates to
/// <see cref="IsolatedStorageFile.OpenFile(string, FileMode, FileAccess, FileShare)"/>; no
/// stream is cached so the file can be opened many times concurrently subject to the supplied
/// share mode.
/// </summary>
[DebuggerDisplay("[F] - {Path}")]
internal sealed class IsolatedStorageFileSystemFile : IsolatedStorageFileSystemInfo, IFileSystemFile
{
    public IsolatedStorageFileSystemFile(
        IsolatedStorageFileSystem fileSystem,
        IsolatedStorageFile storage,
        FileSystemPath path)
        : base(fileSystem, storage, path)
    {
    }

    /// <inheritdoc />
    public FileName Name => Path.GetFileName() ?? throw new InvalidOperationException(
        $"Path '{Path}' does not contain a file name.");

    /// <inheritdoc />
    public Size Size
    {
        get
        {
            // IsolatedStorageFile has no Size accessor — open the file in shared-read mode and
            // measure the stream length. Returns 0 for files that no longer exist so callers can
            // chain after a delete without observing an exception.
            try
            {
                using var stream = Storage.OpenFile(StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return stream.Length;
            }
            catch (FileNotFoundException)
            {
                return 0L;
            }
            catch (DirectoryNotFoundException)
            {
                return 0L;
            }
            catch (IsolatedStorageException)
            {
                return 0L;
            }
        }
    }

    /// <inheritdoc />
    public IFileSystemDirectory Directory
    {
        get
        {
            string text = Path.ToString();
            int lastSep = text.LastIndexOf('/');
            FileSystemPath parentPath = lastSep <= 0
                ? IsolatedStoragePathHelper.Root
                : text.Substring(0, lastSep);

            return new IsolatedStorageFileSystemDirectory(FileSystem, Storage, parentPath);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Returns a polling token scoped to this file only. The cadence is configured via
    /// <see cref="IsolatedStorageFileSystemOptions.WatchPollInterval"/>; see
    /// <see cref="IsolatedStorageFileSystem.Watch(Glob?)"/> for details.
    /// </remarks>
    public IFileSystemEventToken Watch() => FileSystem.CreateFileWatchToken(Path);

    /// <inheritdoc />
    public Stream Open() => Open(FileMode.Open);

    /// <inheritdoc />
    public Stream Open(FileMode fileMode)
        => Open(fileMode, FileSystem.IsReadOnly ? FileAccess.Read : FileAccess.ReadWrite);

    /// <inheritdoc />
    public Stream Open(FileMode fileMode, FileAccess fileAccess) => Open(fileMode, fileAccess, FileShare.None);

    /// <inheritdoc />
    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
    {
        if (FileSystem.IsReadOnly && (fileAccess != FileAccess.Read || fileMode != FileMode.Open))
        {
            FileSystemException.ThrowReadOnly(nameof(Open));
        }

        try
        {
            return Storage.OpenFile(StorePath, fileMode, fileAccess, fileShare);
        }
        catch (FileNotFoundException ex)
        {
            FileSystemException.ThrowFileNotFound(Path, ex);
            throw; // unreachable — keeps the compiler aware of the non-null return.
        }
        catch (DirectoryNotFoundException ex)
        {
            FileSystemException.ThrowDirectoryNotFound(Path, ex);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            FileSystemException.ThrowAccessDenied(Path, ex);
            throw;
        }
        catch (IsolatedStorageException ex)
        {
            FileSystemException.ThrowAccessDenied(Path, ex);
            throw;
        }
    }
}
