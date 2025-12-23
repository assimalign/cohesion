using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Internal;
using Cohesion.Internal;

public sealed class GlobMatcherBuilder : IGlobMatcherBuilder
{
    private readonly List<GlobContext> _includes;
    private readonly List<GlobContext> _excludes;
    private readonly GlobMatcherOptions _options;

    #region Constructors

    public GlobMatcherBuilder()
    {
        _includes = new List<GlobContext>();
        _excludes = new List<GlobContext>();
        _options ??= new GlobMatcherOptions();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    public GlobMatcherBuilder(GlobMatcherOptions options) : this()
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Adds an include pattern to match.
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public GlobMatcherBuilder AddInclude(Glob pattern)
    {
        _includes.Add(new GlobContext(pattern)
        {
            IgnoreCase = _options.IgnoreCase,
            CultureInfo = _options.CultureInfo
        });
        return this;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    public GlobMatcherBuilder AddExclude(Glob pattern)
    {
        _excludes.Add(new GlobContext(pattern)
        {
            IgnoreCase = _options.IgnoreCase,
            CultureInfo = _options.CultureInfo
        });
        return this;
    }

    public IGlobMatcher Build()
    {
        if (_includes.Count == 0 && _excludes.Count == 0)
        {
            ThrowHelper.ThrowInvalidOperationException("At least one Glob pattern must be added.");
        }

        return new GlobMatcher(
            _includes,
            _excludes,
            _options);
    }

    #endregion

    IGlobMatcherBuilder IGlobMatcherBuilder.AddInclude(Glob pattern)
    {
        return AddInclude(pattern);
    }
    IGlobMatcherBuilder IGlobMatcherBuilder.AddExclude(Glob pattern)
    {
        return AddExclude(pattern);
    }
}
