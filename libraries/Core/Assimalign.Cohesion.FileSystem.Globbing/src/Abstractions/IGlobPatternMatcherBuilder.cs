using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// 
/// </summary>
public interface IGlobPatternMatcherBuilder
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    IGlobPatternMatcherBuilder AddInclude(Glob pattern);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    IGlobPatternMatcherBuilder AddExclude(Glob pattern);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IGlobPatternMatcher Build();
}
