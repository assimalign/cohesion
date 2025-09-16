using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public sealed class FileSystemEnumerationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to recurse into subdirectories.
    /// </summary>
    public bool Recurse { get; set; }

    /// <summary>
    /// 
    /// </summary>
    public FileAttributes  AttributesToSkip { get; set; }
}
