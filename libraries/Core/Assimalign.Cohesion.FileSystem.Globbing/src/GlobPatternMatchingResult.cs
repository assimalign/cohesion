using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// Represents a collection of <see cref="GlobPatternMatch" />
/// </summary>
public class GlobPatternMatchingResult
{
    /// <summary>
    /// Initializes the result with a collection of <see cref="GlobPatternMatch" />
    /// </summary>
    /// <param name="files">A collection of <see cref="GlobPatternMatch" /></param>
    public GlobPatternMatchingResult(IEnumerable<IFileSystemInfo> files)
    {
        Files = files;
    }

    /// <summary>
    /// Gets a value that determines if this instance of <see cref="GlobPatternMatchingResult"/> has any matches.
    /// </summary>
    public bool HasMatches => Files.Any();

    /// <summary>
    /// A collection of <see cref="GlobPatternMatch" />
    /// </summary>
    public IEnumerable<IFileSystemInfo> Files { get; set; }

    /// <summary>
    /// Returns an empty result set.
    /// </summary>
    public static GlobPatternMatchingResult Empty { get; } = new GlobPatternMatchingResult(Array.Empty<IFileSystemInfo>());
}
