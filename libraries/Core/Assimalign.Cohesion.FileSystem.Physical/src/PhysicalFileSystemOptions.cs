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
        if (OperatingSystem.IsLinux())
        {
            Drive = "/";
        }
        if (OperatingSystem.IsWindows())
        {
            Drive = "C:/";
        }
    }

    /// <summary>
    /// The drive to initialize the file system from.
    /// </summary>
    public string? Drive { get; set; }

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;
}
