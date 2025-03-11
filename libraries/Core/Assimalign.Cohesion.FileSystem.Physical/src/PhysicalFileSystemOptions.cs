using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public class PhysicalFileSystemOptions
{
    public PhysicalFileSystemOptions()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsIOS() || OperatingSystem.IsMacOS())
        {
            Root = "/";
        }
        if (OperatingSystem.IsWindows())
        {
            Root = "C:/";
        }
    }

    /// <summary>
    /// The drive to initialize the file system from.
    /// </summary>
    public FileSystemPath Root { get; set; }

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// 
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Returns the default options.
    /// </summary>
    public static PhysicalFileSystemOptions Default { get; } = new PhysicalFileSystemOptions();
}
