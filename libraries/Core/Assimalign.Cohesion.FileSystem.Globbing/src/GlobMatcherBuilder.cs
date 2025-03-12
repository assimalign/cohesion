using System;
using System.IO;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

using Internal;

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
        _options = new GlobMatcherOptions();
    }

    public GlobMatcherBuilder(GlobMatcherOptions options) : this()
    {
        _options = options;
    }

    #endregion

    IGlobMatcherBuilder IGlobMatcherBuilder.AddInclude(Glob pattern)
    {
        _includes.Add(new GlobContext(pattern)
        {
            IgnoreCase = _options.IgnoreCase,
            CultureInfo = _options.CultureInfo
        });

        return this;
    }
    IGlobMatcherBuilder IGlobMatcherBuilder.AddExclude(Glob pattern)
    {
        _excludes.Add(new GlobContext(pattern)
        {
            IgnoreCase = _options.IgnoreCase,
            CultureInfo = _options.CultureInfo
        });

        return this;
    }
    IGlobMatcher IGlobMatcherBuilder.Build()
    {
        return new GlobMatcher(
            _includes, 
            _excludes, 
            _options);
    }
}
