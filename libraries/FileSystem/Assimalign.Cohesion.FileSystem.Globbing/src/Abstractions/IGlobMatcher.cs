using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// 
/// </summary>
public interface IGlobMatcher
{
    /// <summary>
    /// Checks whether the path matches.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool IsMatch(FileSystemPath path);

    /// <summary>
    /// Checks whether the file path matches.
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    bool IsMatch(IFileSystemFile file);

    /// <summary>
    /// Checks whether the directory path matches.
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    bool IsMatch(IFileSystemDirectory directory);

    /// <summary>
    /// Returns a collection of matches from the given directory.
    /// </summary>
    /// <param name="directory">The directory to begin globing from.</param>
    /// <returns></returns>
    GlobMatchResults Match(IFileSystemDirectory directory);
}
