using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing.Internal;

using Cohesion.Internal;

internal class GlobMatcher : IGlobMatcher
{
    private readonly IEnumerable<IGlobContext> _includes;
    private readonly IEnumerable<IGlobContext> _excludes;
    private readonly bool _excludeDirectories;

    public GlobMatcher(
        IEnumerable<IGlobContext> includes,
        IEnumerable<IGlobContext> excludes,
        GlobMatcherOptions options)
    {
        _includes = includes;
        _excludes = excludes;
        _excludeDirectories = options.ExcludeDirectories;
    }

    public bool IsMatch(FileSystemPath path)
    {
        bool result = false;

        foreach (var include in _includes)
        {
            if (include.Test(path))
            {
                result = true;
                break;
            }
        }

        if (!result)
        {
            return false;
        }

        foreach (var exclude in _excludes)
        {
            if (exclude.Test(path))
            {
                result = false;
                break;
            }
        }

        return result;
    }

    public bool IsMatch(IFileSystemFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return IsMatch(file.Path);
    }

    public bool IsMatch(IFileSystemDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return IsMatch(directory.Path);
    }

    public GlobMatchResults Match(IFileSystemDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var matches = new List<IFileSystemInfo>();

        Recurse(directory, matches);

        if (matches.Count > 0)
        {
            return new GlobMatchResults(matches.AsReadOnly());
        }

        return GlobMatchResults.Empty;
    }

    private void Recurse(IFileSystemDirectory directory, List<IFileSystemInfo> matches)
    {
        if (IsMatch(directory.Path) && !_excludeDirectories)
        {
            matches.Add(directory);
        }

        foreach (var item in directory)
        {
            switch (item)
            {
                case IFileSystemFile file when IsMatch(file.Path):
                    matches.Add(file);
                    break;

                case IFileSystemDirectory dir when IsMatch(dir.Path):
                    Recurse(dir, matches);
                    break;
            }
        }
    }
}
