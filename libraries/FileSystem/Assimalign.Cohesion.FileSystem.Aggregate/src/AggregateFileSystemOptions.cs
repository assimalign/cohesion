using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem;

using Assimalign.Cohesion.FileSystem.Internal;

/// <summary>
/// Configuration for an <see cref="AggregateFileSystem"/>. Holds the set of mounted providers,
/// the display name, and the read-only flag. Constructed by <see cref="AggregateFileSystemBuilder"/>
/// or supplied directly to the file system constructor.
/// </summary>
public sealed class AggregateFileSystemOptions
{
    /// <summary>
    /// Display name surfaced through <see cref="IFileSystem.Name"/>. Defaults to
    /// <c>"AggregateFileSystem"</c> when unset.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// When <see langword="true"/> every mutating operation is rejected with a
    /// <see cref="FileSystemException"/> whose code is
    /// <see cref="FileSystemErrorCode.ReadOnly"/>, regardless of whether individual mounted
    /// providers would have accepted the call.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// The mount table. Each entry binds a virtual <see cref="FileSystemPath"/> prefix to a
    /// concrete <see cref="IFileSystem"/>. Resolution uses longest-prefix matching.
    /// </summary>
    internal List<AggregateMount> Mounts { get; } = new();
}
