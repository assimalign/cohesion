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
    /// The max size of the in memory file system. The default is 32 MB
    /// </summary>
    public Size Size { get; set; } = Size.FromMegabytes(32);

    /// <summary>
    /// Specify the culture to use when comparing the path.
    /// </summary>
    public CultureInfo? CultureInfo { get; set; }

    /// <summary>
    /// Sets whther the In Memory File System should be case-insensitive.
    /// </summary>
    public bool IgnoreCase { get; set; }

    /// <summary>
    /// Set a root directory name.
    /// </summary>
    public DirectoryName RootName { get; set; } = DirectoryName.Root;

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;
}