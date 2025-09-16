using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public class InMemoryFileSystemOptions
{
    /// <summary>
    /// A name for the in memory file system instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Sets whether the in memory file system is read-only.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Specify the lock timeout on File System objects. The default is 30 seconds.
    /// </summary>
    public TimeSpan LockTmieout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The max size of the in memory file system. The default is 32 MB
    /// </summary>
    public Size Size { get; set; } = Size.FromMegabytes(32);

    /// <summary>
    /// Specify the culture to use when comparing the path.
    /// </summary>
    public StringComparison Comparison { get; set; }

    /// <summary>
    /// Set a root directory name.
    /// </summary>
    public DirectoryName RootName { get; set; } = DirectoryName.Root;

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;
}