using System;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// 
/// </summary>
public interface IGlobMatcherBuilder
{
    /// <summary>
    /// Adds a glob pattern to include files/directories that match.
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    IGlobMatcherBuilder AddInclude(Glob pattern);

    /// <summary>
    /// Adds a glob pattern to excludes files/directories that match.
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    IGlobMatcherBuilder AddExclude(Glob pattern);

    /// <summary>
    /// Builds the <see cref="IGlobMatcher"/>.
    /// </summary>
    /// <returns></returns>
    IGlobMatcher Build();
}
