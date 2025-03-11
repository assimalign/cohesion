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
}
