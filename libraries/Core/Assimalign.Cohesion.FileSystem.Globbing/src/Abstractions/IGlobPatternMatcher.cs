using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// 
/// </summary>
public interface IGlobPatternMatcher
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool IsMatch(FileSystemPath path);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    GlobPatternMatchingResult Match(IFileSystemDirectory directory);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    GlobPatternMatchingResult MatchExact(IFileSystemDirectory directory);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    GlobPatternMatchingResult MatchExact(IFileSystemFile file);
}
