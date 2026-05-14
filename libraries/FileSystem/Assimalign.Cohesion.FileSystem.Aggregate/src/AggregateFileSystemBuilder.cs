using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Fluent builder for <see cref="AggregateFileSystem"/>. Use <see cref="Mount(FileSystemPath, IFileSystem, bool)"/>
/// to register mounted providers and <see cref="Build"/> to materialize the aggregate. The
/// builder is single-use; calling <see cref="Build"/> a second time throws
/// <see cref="InvalidOperationException"/> to keep ownership semantics unambiguous.
/// </summary>
public sealed class AggregateFileSystemBuilder
{
    private readonly AggregateFileSystemOptions _options = new();
    private bool _built;

    /// <summary>
    /// Sets the display name surfaced through <see cref="IFileSystem.Name"/>.
    /// </summary>
    public AggregateFileSystemBuilder WithName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        EnsureNotBuilt();
        _options.Name = name;
        return this;
    }

    /// <summary>
    /// Configures the aggregate to reject every mutating operation with
    /// <see cref="FileSystemErrorCode.ReadOnly"/>, even when individual mounted providers would
    /// have accepted it. Defaults to <see langword="false"/>.
    /// </summary>
    public AggregateFileSystemBuilder AsReadOnly(bool isReadOnly = true)
    {
        EnsureNotBuilt();
        _options.IsReadOnly = isReadOnly;
        return this;
    }

    /// <summary>
    /// Registers a mounted provider at <paramref name="mountPath"/>.
    /// </summary>
    /// <param name="mountPath">
    /// Absolute aggregate-side path under which the provider's contents will appear. Must not be
    /// empty; relative paths are rooted at "/" automatically.
    /// </param>
    /// <param name="fileSystem">The provider to mount.</param>
    /// <param name="ownsFileSystem">
    /// When <see langword="true"/> the aggregate disposes <paramref name="fileSystem"/> with
    /// itself. When <see langword="false"/> (the default) the caller retains ownership and is
    /// responsible for disposing the underlying provider.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="mountPath"/> is empty or when another mount already exists at
    /// the exact same path.
    /// </exception>
    public AggregateFileSystemBuilder Mount(FileSystemPath mountPath, IFileSystem fileSystem, bool ownsFileSystem = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        EnsureNotBuilt();

        var mount = new AggregateMount(mountPath, fileSystem, ownsFileSystem);

        foreach (var existing in _options.Mounts)
        {
            if (existing.MountPath.Equals(mount.MountPath))
            {
                throw new ArgumentException(
                    $"A mount is already registered at '{mount.MountPath}'. Use a different mount path or remove the existing entry before adding a new one.",
                    nameof(mountPath));
            }
        }

        _options.Mounts.Add(mount);
        return this;
    }

    /// <summary>
    /// Materializes the configured <see cref="AggregateFileSystem"/>. The builder transitions
    /// into a built state after this call; subsequent <see cref="Build"/> or
    /// <see cref="Mount(FileSystemPath, IFileSystem, bool)"/> calls throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public AggregateFileSystem Build()
    {
        EnsureNotBuilt();
        _built = true;
        return new AggregateFileSystem(_options);
    }

    private void EnsureNotBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException(
                "AggregateFileSystemBuilder has already produced a file system. Create a new builder for additional aggregates.");
        }
    }
}
