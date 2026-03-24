using System;
using System.Globalization;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using Cohesion.Internal;

internal class GlobContext : IGlobContext
{
    public GlobContext(Glob glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        Glob = glob;
    }

    public Glob Glob { get; }
    public bool IgnoreCase { get; init; } = true;
    public CultureInfo CultureInfo { get; init; } = CultureInfo.InvariantCulture;
    public bool Test(FileSystemPath path)
    {
        return Glob.IsMatch(path, CultureInfo, IgnoreCase);
    }
    public bool Test(IFileSystemFile file)
    {
        return Test(file.Path);
    }
    public bool Test(IFileSystemDirectory directory)
    {
        return Test(directory.Path);
    }
}