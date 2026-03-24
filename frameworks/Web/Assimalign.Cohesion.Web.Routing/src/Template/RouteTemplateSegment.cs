using Assimalign.Cohesion.Web.Routing.Patterns;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Template;

/// <summary>
/// Represents a segment of a route template.
/// </summary>
[DebuggerDisplay("{DebuggerToString()}")]
public class RouteTemplateSegment
{
    /// <summary>
    /// Constructs a new <see cref="RouteTemplateSegment"/> instance.
    /// </summary>
    public RouteTemplateSegment()
    {
        Parts = new List<RouteTemplatePart>();
    }

    /// <summary>
    /// Constructs a new <see cref="RouteTemplateSegment"/> instance given another <see cref="RoutePatternPathSegment"/>.
    /// </summary>
    /// <param name="other">A <see cref="RoutePatternPathSegment"/> instance.</param>
    public RouteTemplateSegment(RoutePatternPathSegment other)
    {
        ArgumentNullException.ThrowIfNull(other);

        var partCount = other.Segments.Count;
        Parts = new List<RouteTemplatePart>(partCount);
        for (var i = 0; i < partCount; i++)
        {
            Parts.Add(new RouteTemplatePart(other.Segments[i]));
        }
    }

    /// <summary>
    /// <see langword="true"/> if the segment contains a single entry.
    /// </summary>
    public bool IsSimple => Parts.Count == 1;

    /// <summary>
    /// Gets the list of individual parts in the template segment.
    /// </summary>
    public List<RouteTemplatePart> Parts { get; }

    internal string DebuggerToString()
    {
        return string.Join(string.Empty, Parts.Select(p => p.DebuggerToString()));
    }

    /// <summary>
    /// Returns a <see cref="RoutePatternPathSegment"/> for the template segment.
    /// </summary>
    /// <returns>A <see cref="RoutePatternPathSegment"/> instance.</returns>
    public RoutePatternPathSegment ToRoutePatternPathSegment()
    {
        var parts = Parts.Select(p => p.ToRoutePatternPart());
        return RoutePatternFactory.Segment(parts);
    }
}
