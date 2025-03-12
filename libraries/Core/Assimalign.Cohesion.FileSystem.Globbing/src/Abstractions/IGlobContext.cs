using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;


public interface IGlobContext
{
    /// <summary>
    /// The glob pattern to test.
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
