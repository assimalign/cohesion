using System;
using System.IO;
using System.Globalization;

namespace Assimalign.Cohesion.FileSystem.Globbing;

public class GlobMatcherOptions
{
    /// <summary>
    /// The default is true - <i>This is due to <see cref="FileSystemPath"/> being case-insensitive. </i>
    /// </summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>
    /// Provide culture info when comparing paths.
    /// </summary>
    public CultureInfo CultureInfo { get; set; } = CultureInfo.InvariantCulture;

    /// <summary>
    /// Excludes directories from <see cref="GlobMatchResults"/>. This is used when 
    /// filtering files based on a directory glob pattern.
    /// </summary>
    public bool ExcludeDirectories { get; set; }
}
