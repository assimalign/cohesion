using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

[DebuggerDisplay("{DebuggerToString()}")]
public sealed class RoutePatternPathSegment
{
    internal RoutePatternPathSegment(IReadOnlyList<RoutePatternSegment> segments)
    {
        Segments = segments;
    }

    /// <summary>
    /// Returns <c>true</c> if the segment contains a single part;
    /// otherwise returns <c>false</c>.
    /// </summary>
    public bool IsSimple => Segments.Count == 1;

    /// <summary>
    /// Gets the list of parts in this segment.
    /// </summary>
    public IReadOnlyList<RoutePatternSegment> Segments { get; }

    internal string DebuggerToString()
    {
        return DebuggerToString(Segments);
    }

    internal static string DebuggerToString(IReadOnlyList<RoutePatternSegment> parts)
    {
        return string.Join(string.Empty, parts.Select(p => p.DebuggerToString()));
    }
}
