using System;
using System.IO;
using System.Linq;

namespace Assimalign.Cohesion.Net;

/// <summary>
/// 
/// </summary>
/// <remarks>
/// Comparing file paths as strings can be dangerous as different OS's have 
/// case-sensitive file system such as linux. The following approach can be 
/// done with a class, or struct as well.
/// </remarks>
public sealed record FilePath
{
    public FilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException($"{nameof(path)} cannot be null or empty");
        }
        else if (System.IO.Path.GetInvalidPathChars().Intersect(path).Any())
        {
            throw new ArgumentException($"{nameof(path)} contains illegal characters");
        }
        else
        {
            Path = System.IO.Path.GetFullPath(path.Trim());
        }
    }

    public string Path { get; }
    public bool Equals(FilePath? other) => Path.Equals(other?.Path, StringComparison.InvariantCultureIgnoreCase);

    public static implicit operator FilePath(string name) => new(name);
    public FileInfo GetInfo() => new FileInfo(Path);
    public FilePath Combine(params string[] paths) => System.IO.Path.Combine(paths.Prepend(Path).ToArray());    
    public override string ToString() => Path;
}