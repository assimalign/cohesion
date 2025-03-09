using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public interface IGlobPattern
{
    /// <summary>
    /// 
    /// </summary>
    Glob Glob { get; }

    /// <summary>
    ///
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool Test(FileSystemPath path);

    /// <summary>
    /// Tests whether the glob pattern matches the given file path
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    bool Test(IFileSystemFile file);

    /// <summary>
    /// Tests whether the glob pattern matches the given directory path
    /// </summary>
    /// <param name="directory"></param>
    /// <returns></returns>
    bool Test(IFileSystemDirectory directory);
}
