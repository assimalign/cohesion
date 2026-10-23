using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("InMemoryFileSystemOptions {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public class InMemoryFileSystemOptions
{
    private FileSystemPath _rootPath = DirectoryName.Root;


    /// <summary>
    /// A name for the in memory file system instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Sets whether the in memory file system is read-only.
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// The max size of the in memory file system. The default is 32 MB
    /// </summary>
    public Size Size { get; set; } = Size.FromMegabytes(32);

    /// <summary>
    /// 
    /// </summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>
    /// Specify the culture to use when comparing the path.
    /// </summary>
    public CultureInfo? CultureInfo { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Set a root directory name.
    /// </summary>
    public FileSystemPath RootPath
    {
        get => _rootPath;
        set
        {
            if (value.StartsWith(".."))
            {
                throw new ArgumentException("A relative path is not allowed.");
            }

            _rootPath = value;
        }
    }

    /// <summary>
    /// The attributes to ignore when enumerating file system.
    /// </summary>
    public FileAttributes IgnoreAttributes { get; set; } = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>
    /// Specify the lock timeout on File System objects. The default is 30 seconds.
    /// </summary>
    //public TimeSpan LockTmieout { get; set; } = TimeSpan.FromSeconds(30);


    private sealed class DebugView
    {
        private readonly InMemoryFileSystemOptions _target;

        public DebugView(InMemoryFileSystemOptions value)
        {
            _target = value;
        }

        public string? Name => _target.Name;
        public bool IsReadOnly => _target.IsReadOnly;
        public Size Size => _target.Size;
        public bool IgnoreCase => _target.IgnoreCase;
        public CultureInfo? CultureInfo => _target.CultureInfo;
        public FileSystemPath RootPath => _target.RootPath;
        public FileAttributes IgnoreAttributes => _target.IgnoreAttributes;
    }
}