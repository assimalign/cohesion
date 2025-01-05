using System;
using System.Linq;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// Represents a collection of <see cref="FilePatternMatch" />
/// </summary>
public class FilePatternMatchingResult
{
    /// <summary>
    /// Initializes the result with a collection of <see cref="FilePatternMatch" />
    /// </summary>
    /// <param name="files">A collection of <see cref="FilePatternMatch" /></param>
    public FilePatternMatchingResult(IEnumerable<FilePatternMatch> files)
        : this(files, hasMatches: files.Any())
    {
        Files = files;
    }

    /// <summary>
    /// Initializes the result with a collection of <see cref="FilePatternMatch" />
    /// </summary>
    /// <param name="files">A collection of <see cref="FilePatternMatch" /></param>
    /// <param name="hasMatches">A value that determines if <see cref="FilePatternMatchingResult"/> has any matches.</param>
    public FilePatternMatchingResult(IEnumerable<FilePatternMatch> files, bool hasMatches)
    {
        Files = files;
        HasMatches = hasMatches;
    }

    /// <summary>
    /// A collection of <see cref="FilePatternMatch" />
    /// </summary>
    public IEnumerable<FilePatternMatch> Files { get; set; }

    /// <summary>
    /// Gets a value that determines if this instance of <see cref="FilePatternMatchingResult"/> has any matches.
    /// </summary>
    public bool HasMatches { get; }
}
