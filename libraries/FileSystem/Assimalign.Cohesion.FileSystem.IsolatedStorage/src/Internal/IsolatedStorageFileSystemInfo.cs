using System;
using System.IO;
using System.IO.IsolatedStorage;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Base wrapper exposing common <see cref="IFileSystemInfo"/> properties on entries inside an
/// <see cref="IsolatedStorageFile"/>. The isolated store does not surface a rich
/// <see cref="System.IO.FileSystemInfo"/> object, so subclasses cache the absolute
/// <see cref="FileSystemPath"/> and look up timestamps on demand.
/// </summary>
internal abstract class IsolatedStorageFileSystemInfo : IFileSystemInfo
{
    private readonly IsolatedStorageFileSystem _fileSystem;
    private readonly IsolatedStorageFile _storage;
    private readonly FileSystemPath _path;

    protected IsolatedStorageFileSystemInfo(
        IsolatedStorageFileSystem fileSystem,
        IsolatedStorageFile storage,
        FileSystemPath path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(storage);

        _fileSystem = fileSystem;
        _storage = storage;
        _path = IsolatedStoragePathHelper.ToAbsolute(path);
    }

    /// <inheritdoc />
    public FileSystemPath Path => _path;

    /// <summary>
    /// The relative store-side path (no leading separator) used when calling into
    /// <see cref="IsolatedStorageFile"/> directly. The root maps to <see cref="string.Empty"/>.
    /// </summary>
    protected string StorePath => IsolatedStoragePathHelper.ToStorePath(_path);

    /// <summary>
    /// The underlying <see cref="IsolatedStorageFile"/>. Subclasses use this to read timestamps
    /// and open streams; callers do not see it.
    /// </summary>
    protected IsolatedStorageFile Storage => _storage;

    /// <inheritdoc />
    public DateTime CreatedOn => SafeTime(() => _storage.GetCreationTime(StorePath).LocalDateTime);

    /// <inheritdoc />
    public DateTime UpdatedOn => SafeTime(() => _storage.GetLastWriteTime(StorePath).LocalDateTime);

    /// <inheritdoc />
    public DateTime AccessedOn => SafeTime(() => _storage.GetLastAccessTime(StorePath).LocalDateTime);

    /// <inheritdoc />
    public IsolatedStorageFileSystem FileSystem => _fileSystem;

    IFileSystem IFileSystemInfo.FileSystem => FileSystem;

    /// <summary>
    /// File attributes are not exposed by <see cref="IsolatedStorageFile"/>. Accessors throw
    /// <see cref="NotSupportedException"/> to keep the unsupported capability explicit and
    /// deterministic across callers.
    /// </summary>
    public FileAttributes Attributes => throw new NotSupportedException(
        "FileAttributes are not exposed by IsolatedStorageFile and are not supported by IsolatedStorageFileSystem.");

    /// <inheritdoc />
    public void SetAttributes(FileAttributes attributes) => throw new NotSupportedException(
        "Setting FileAttributes is not supported by IsolatedStorageFileSystem.");

    /// <summary>
    /// Wraps a timestamp accessor so that a missing entry surfaces as
    /// <see cref="DateTime.MinValue"/> instead of an opaque <see cref="IsolatedStorageException"/>.
    /// Mirrors how <see cref="System.IO.FileSystemInfo"/> handles deleted paths.
    /// </summary>
    private static DateTime SafeTime(Func<DateTime> read)
    {
        try
        {
            return read();
        }
        catch (IsolatedStorageException)
        {
            return DateTime.MinValue;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
