using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Internal;

public sealed class GlobPatternMatcher : IGlobPatternMatcher, IGlobPatternMatcherBuilder
{
    private readonly IEnumerable<GlobPattern> _includes;
    private readonly IEnumerable<GlobPattern> _excludes;
    private readonly StringComparison _comparison;

    #region Constructors

    private GlobPatternMatcher()
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
    public GlobPatternMatcher AddInclude(Glob pattern)
    {
        _includes.Add(CreatePattern(pattern, true));

        return this;
    }
    
    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public GlobPatternMatcher AddExclude(Glob pattern)
    {
        _excludes.Add(CreatePattern(pattern, false));

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

    private IGlobPattern CreatePattern(Glob pattern, bool include)
    {
        var segments = new List<GlobSegment>();

        List<GlobSegment> startsWith = null!;
        List<IList<GlobSegment>> contains = null!;
        List<GlobSegment> endsWith = null!;

        for (int i = 0; i < pattern.Count; i++)
        {
            var segment = pattern[i];

            if (segment.Kind == GlobSegmentKind.RecursiveWildcard)
            {
                if (startsWith is null)
                {
                    startsWith = new List<GlobSegment>(segments);
                    endsWith = new List<GlobSegment>();
                    contains = new List<IList<GlobSegment>>();
                }
                else if (endsWith!.Count != 0)
                {
                    contains!.Add(endsWith);
                    endsWith = new List<GlobSegment>();
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
            return include ? 
                new LinearGlobPatternInclude(pattern, _comparison) : 
                new LinearGlobPatternExclude(pattern, _comparison);
        }
        else
        {
            return include ?
                new RaggedGlobPatternInclude(
                    pattern,
                    [.. startsWith],
                    [.. contains.Select(p => p.ToArray())],
                    [.. endsWith ?? []],
                    _comparison) :
                new RaggedGlobPatternExclude(
                    pattern,
                    [.. startsWith],
                    [.. contains.Select(p => p.ToArray())],
                    [.. endsWith ?? []],
                    _comparison);
        }
    }
}
