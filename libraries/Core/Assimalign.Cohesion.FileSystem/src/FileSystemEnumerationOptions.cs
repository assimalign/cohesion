using System.IO;

namespace Assimalign.Cohesion.FileSystem;

/// <summary>
/// 
/// </summary>
public sealed class FileSystemEnumerationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to recurse into subdirectories and files.
    /// </summary>
    public bool Recurse { get; set; }

    /// <summary>
    /// The path to begin enumeration.
    /// </summary>
    public FileSystemPath? Path { get; set; }

    /// <summary>
    /// Attributes to skip during enumeration.
    /// </summary>
    public FileAttributes AttributesToSkip { get; set; }
}
