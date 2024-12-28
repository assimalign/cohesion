using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

public interface IReadOnlyFileSystem : IEnumerable<IFileSystemInfo>, IAsyncDisposable
{
    /// <summary>
    /// 
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The total size of the file system.
    /// </summary>
    Size Size { get; }
    /// <summary>
    /// The available space.
    /// </summary>
    Size Space { get; }
    /// <summary>
    /// The amount of space used.
    /// </summary>
    Size SpaceUsed { get; }
    /// <summary>
    /// The root directory of the file system.
    /// </summary>
    IFileSystemDirectory RootDirectory { get; }
    /// <summary>
    /// Checks whether the given path exists in the file system.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool Exist(Path path);
    /// <summary>
    /// Enumerates through all the directories in the file system.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemDirectory> GetDirectories();
    /// <summary>
    /// Enumerates all the files in the File System.
    /// </summary>
    /// <returns></returns>
    IEnumerable<IFileSystemFile> GetFiles();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemDirectory GetDirectory(Path path);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IFileSystemFile GetFile(Path path);
}
