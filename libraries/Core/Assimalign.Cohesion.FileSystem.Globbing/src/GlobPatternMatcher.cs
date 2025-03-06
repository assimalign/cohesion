using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Internal;

public sealed class GlobPatternMatcher
{
    private readonly List<IGlobPattern> _includes;
    private readonly List<IGlobPattern> _excludes;
    private readonly StringComparison _comparison;

    #region Constructors

    public GlobPatternMatcher()
    {
        _includes = new List<IGlobPattern>();
        _excludes = new List<IGlobPattern>();
        _comparison = StringComparison.Ordinal;
    }
    public GlobPatternMatcher(StringComparison comparison) : this()
    {
        _comparison = comparison;
    }

    #endregion

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public GlobPatternMatcher AddInclude(FileSystemPath pattern)
    {
        var segments = FileSystemPathSegment.Parse(pattern);

        _includes.Add(CreatePattern(segments));

        return this;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public GlobPatternMatcher AddExclude(FileSystemPath pattern)
    {
        var segments = FileSystemPathSegment.Parse(pattern);

        _excludes.Add(CreatePattern(segments));

        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    public GlobPatternMatchingResult Match(IFileSystemDirectory info)
    {
        var context = new GlobMatcherContext(
            _includes,
            _excludes,
            _comparison);

        return context.Match(info);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="info"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public GlobPatternMatchingResult MatchExact(IFileSystemInfo info)
    {
        var context = new GlobMatcherContext(
            _includes, 
            _excludes,
            _comparison);

        return info switch
        {
            IFileSystemFile file => context.MatchExact(file),
            IFileSystemDirectory directory => context.MatchExact(directory),
            _ => throw new NotSupportedException()
        };
    }

    /// <summary>
    /// Checks whether the provided file matches any of the include patterns.
    /// </summary>
    /// <param name="file"></param>
    /// <returns></returns>
    public bool IsMatch(IFileSystemFile file)
    {
        return MatchExact(file).HasMatches;
    }


    private IGlobPattern CreatePattern(FileSystemPathSegment[] items)
    {
        var segments = new List<FileSystemPathSegment>();

        List<FileSystemPathSegment> startsWith = null!;
        List<IList<FileSystemPathSegment>> contains = null!;
        List<FileSystemPathSegment> endsWith = null!;

        for (int i = 0; i < items.Length; i++)
        {
            var segment = items[i];

            if (segment is FileSystemPathSegment.RecursiveWildcardSegment)
            {
                if (startsWith is null)
                {
                    startsWith = new List<FileSystemPathSegment>(segments);
                    endsWith = new List<FileSystemPathSegment>();
                    contains = new List<IList<FileSystemPathSegment>>();
                }
                else if (endsWith!.Count != 0)
                {
                    contains!.Add(endsWith);
                    endsWith = new List<FileSystemPathSegment>();
                }
            }
            else if (endsWith != null)
            {
                endsWith.Add(segment);
            }

            segments.Add(segment);
        }
        if (startsWith == null)
        {
            return new LinearGlobPattern(segments.ToArray());
        }
        else
        {
            return new RaggedGlobPattern(
                [.. segments],
                [.. startsWith],
                [.. endsWith ?? []],
                [.. contains.Select(p => p.ToArray())]);
        }
    }
}
