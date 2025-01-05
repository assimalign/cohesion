using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// A file system factory that creates named file systems.
/// </summary>
public interface IFileSystemFactory
{
    /// <summary>
    /// Creates a named file 
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    IFileSystem Create(string name);
    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="TFileSystem"></typeparam>
    /// <returns></returns>
    IFileSystem Create<TFileSystem>() where TFileSystem : IFileSystem;
}
