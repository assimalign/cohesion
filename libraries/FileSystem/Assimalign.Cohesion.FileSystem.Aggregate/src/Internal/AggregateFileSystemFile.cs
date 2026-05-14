using System;
using System.Diagnostics;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Wraps an <see cref="IFileSystemFile"/> from a mounted provider so the path surface stays in
/// aggregate-space (e.g. "/data/foo.txt") instead of the provider-relative form (e.g. "/foo.txt").
/// All file-system operations are forwarded to the underlying provider.
/// </summary>
[DebuggerDisplay("[F] - {Path}")]
internal sealed class AggregateFileSystemFile : IFileSystemFile
{
    private readonly AggregateFileSystem _aggregate;
    private readonly AggregateMount _mount;
    private readonly IFileSystemFile _inner;
    private readonly FileSystemPath _aggregatePath;

    public AggregateFileSystemFile(
        AggregateFileSystem aggregate,
        AggregateMount mount,
        IFileSystemFile inner,
        FileSystemPath aggregatePath)
    {
        _aggregate = aggregate;
        _mount = mount;
        _inner = inner;
        _aggregatePath = aggregatePath;
    }

    /// <inheritdoc />
    public FileSystemPath Path => _aggregatePath;

    /// <inheritdoc />
    public DateTime CreatedOn => _inner.CreatedOn;

    /// <inheritdoc />
    public DateTime UpdatedOn => _inner.UpdatedOn;

    /// <inheritdoc />
    public DateTime AccessedOn => _inner.AccessedOn;

    /// <inheritdoc />
    public FileAttributes Attributes => _inner.Attributes;

    /// <inheritdoc />
    public IFileSystem FileSystem => _aggregate;

    /// <inheritdoc />
    public FileName Name => _inner.Name;

    /// <inheritdoc />
    public Size Size => _inner.Size;

    /// <inheritdoc />
    public IFileSystemDirectory Directory
    {
        get
        {
            // Translate the parent's provider-side path back to aggregate space and wrap it.
            FileSystemPath providerParent = _inner.Directory.Path;
            FileSystemPath aggregateParent = _mount.ToAggregatePath(providerParent);
            return new AggregateFileSystemDirectory(_aggregate, _mount, _inner.Directory, aggregateParent);
        }
    }

    /// <inheritdoc />
    public void SetAttributes(FileAttributes attributes) => _inner.SetAttributes(attributes);

    /// <inheritdoc />
    public Stream Open() => _inner.Open();

    /// <inheritdoc />
    public Stream Open(FileMode fileMode) => _inner.Open(fileMode);

    /// <inheritdoc />
    public Stream Open(FileMode fileMode, FileAccess fileAccess) => _inner.Open(fileMode, fileAccess);

    /// <inheritdoc />
    public Stream Open(FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
        => _inner.Open(fileMode, fileAccess, fileShare);

    /// <inheritdoc />
    public IFileSystemEventToken Watch() => _inner.Watch();
}
