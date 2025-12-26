using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// Represents a collection of <see cref="IFileSystemInfo" /> matches.
/// </summary>
public sealed class GlobMatchResults
{
    public GlobMatchResults(IEnumerable<IFileSystemInfo> files)
    {
        Files = ArgumentNullException.ThrowIfNull<IEnumerable<IFileSystemInfo>>(files);
    }

    /// <summary>
    /// Gets a value that determines if this instance of <see cref="GlobMatchResults"/> has any matches.
    /// </summary>
    public bool HasMatches => Files.Any();

    /// <summary>
    /// A collection of <see cref="GlobPatternMatch" />
    /// </summary>
    public IEnumerable<IFileSystemInfo> Files { get; set; }

    /// <summary>
    /// Returns an empty result set.
    /// </summary>
    public static GlobMatchResults Empty { get; } = new GlobMatchResults(Array.Empty<IFileSystemInfo>());
}
