using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;


using Assimalign.Cohesion.FileSystem.Globbing.Internal.Utilities;

/// <summary>
/// Represents a file that was matched by searching using a globbing pattern
/// </summary>
public struct GlobPatternMatch : IEquatable<GlobPatternMatch>
{
    /// <summary>
    /// Initializes new instance of <see cref="GlobPatternMatch" />
    /// </summary>
    /// <param name="path">The path to the file matched, relative to the beginning of the matching search pattern.</param>
    /// <param name="stem">The sub-path to the file matched, relative to the first wildcard in the matching search pattern.</param>
    public GlobPatternMatch(FileSystemPath path, FileSystemPath stem)
    {
        Path = path;
        Stem = stem;
    }

    /// <summary>
    /// The path to the file matched, relative to the beginning of the matching search pattern.
    /// </summary>
    /// <remarks>
    /// If the matcher searched for "src/Project/**/*.cs" and the pattern matcher found "src/Project/Interfaces/IFile.cs",
    /// then <see cref="Stem" /> = "Interfaces/IFile.cs" and <see cref="Path" /> = "src/Project/Interfaces/IFile.cs".
    /// </remarks>
    public FileSystemPath Path { get; }

    /// <summary>
    /// The sub-path to the file matched, relative to the first wildcard in the matching search pattern.
    /// </summary>
    /// <remarks>
    /// If the matcher searched for "src/Project/**/*.cs" and the pattern matcher found "src/Project/Interfaces/IFile.cs",
    /// then <see cref="Stem" /> = "Interfaces/IFile.cs" and <see cref="Path" /> = "src/Project/Interfaces/IFile.cs".
    /// </remarks>
    public FileSystemPath Stem { get; }

    /// <summary>
    /// Determines if the specified match is equivalent to the current match using a case-insensitive comparison.
    /// </summary>
    /// <param name="other">The other match to be compared</param>
    /// <returns>True if <see cref="Path" /> and <see cref="Stem" /> are equal using case-insensitive comparison</returns>
    public bool Equals(GlobPatternMatch other)
    {
        return string.Equals(other.Path, Path, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(other.Stem, Stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines if the specified object is equivalent to the current match using a case-insensitive comparison.
    /// </summary>
    /// <param name="obj">The object to be compared</param>
    /// <returns>True when <see cref="Equals(GlobPatternMatch)" /></returns>
    public override bool Equals(object obj)
    {
        return Equals((GlobPatternMatch)obj);
    }

    /// <summary>
    /// Gets a hash for the file pattern match.
    /// </summary>
    /// <returns>Some number</returns>
    public override int GetHashCode() =>
        HashHelpers.Combine(GetHashCode(Path), GetHashCode(Stem));

    private static int GetHashCode(string value) =>
        value != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(value) : 0;
}


