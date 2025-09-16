using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public class PhysicalFileSystemOptions
{
    public PhysicalFileSystemOptions()
    {
        if (OperatingSystem.IsWindows())
        {
            Root = "C:/";
        }
        else
        {
            Root = "/";
        }
    }

    /// <summary>
    /// A friendly name for the file system instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The drive to initialize the file system from.
    /// </summary>
    public FileSystemPath Root { get; set; }

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// Sets the file system to be read-only. The default is false.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Returns the default options.
    /// </summary>
    public static PhysicalFileSystemOptions Default { get; } = new PhysicalFileSystemOptions();
}
