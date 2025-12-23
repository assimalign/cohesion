
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Assimalign.Cohesion.Web.Routing.Template;

using Assimalign.Cohesion.Web.Routing.Patterns;


/// <summary>
/// Represents the template for a route.
/// </summary>
[DebuggerDisplay("{DebuggerToString()}")]
public class RouteTemplate
{
    private const string SeparatorString = "/";

    /// <summary>
    /// Constructs a new <see cref="RouteTemplate"/> instance given <paramref name="other"/>.
    /// </summary>
    /// <param name="other">A <see cref="RoutePattern"/> instance.</param>
    public RouteTemplate(RoutePattern other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // RequiredValues will be ignored. RouteTemplate doesn't support them.

        TemplateText = other.RawText;

        Segments = new List<RouteTemplateSegment>(other.PathSegments.Count);
        foreach (var p in other.PathSegments)
        {
            Segments.Add(new RouteTemplateSegment(p));
        }

        Parameters = new List<RouteTemplatePart>();
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            for (var j = 0; j < segment.Parts.Count; j++)
            {
                var part = segment.Parts[j];
                if (part.IsParameter)
                {
                    Parameters.Add(part);
                }
            }
        }
    }

    /// <summary>
    /// Constructs a a new <see cref="RouteTemplate" /> instance given the <paramref name="template"/> string
    /// and a list of <paramref name="segments"/>. Computes the parameters in the route template.
    /// </summary>
    /// <param name="template">A string representation of the route template.</param>
    /// <param name="segments">A list of <see cref="RouteTemplateSegment"/>.</param>
    public RouteTemplate(string template, List<RouteTemplateSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        TemplateText = template;

        Segments = segments;

        Parameters = new List<RouteTemplatePart>();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = Segments[i];
            for (var j = 0; j < segment.Parts.Count; j++)
            {
                var part = segment.Parts[j];
                if (part.IsParameter)
                {
                    Parameters.Add(part);
                }
            }
        }
    }

    /// <summary>
    /// Gets the string representation of the route template.
    /// </summary>
    public string? TemplateText { get; }

    /// <summary>
    /// Gets the list of <see cref="TemplatePart"/> that represent that parameters defined in the route template.
    /// </summary>
    public IList<RouteTemplatePart> Parameters { get; }

    /// <summary>
    /// Gets the list of <see cref="RouteTemplateSegment"/> that compromise the route template.
    /// </summary>
    public IList<RouteTemplateSegment> Segments { get; }

    /// <summary>
    /// Gets the <see cref="RouteTemplateSegment"/> at a given index.
    /// </summary>
    /// <param name="index">The index of the element to retrieve.</param>
    /// <returns>A <see cref="RouteTemplateSegment"/> instance.</returns>
    public RouteTemplateSegment? GetSegment(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return index >= Segments.Count ? null : Segments[index];
    }

    private string DebuggerToString()
    {
        return string.Join(SeparatorString, Segments.Select(s => s.DebuggerToString()));
    }

    /// <summary>
    /// Gets the parameter matching the given name.
    /// </summary>
    /// <param name="name">The name of the parameter to match.</param>
    /// <returns>The matching parameter or <c>null</c> if no parameter matches the given name.</returns>
    public RouteTemplatePart? GetParameter(string name)
    {
        for (var i = 0; i < Parameters.Count; i++)
        {
            var parameter = Parameters[i];
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts the <see cref="RouteTemplate"/> to the equivalent
    /// <see cref="RoutePattern"/>
    /// </summary>
    /// <returns>A <see cref="RoutePattern"/>.</returns>
    public RoutePattern ToRoutePattern()
    {
        var segments = Segments.Select(s => s.ToRoutePatternPathSegment());
        return RoutePatternFactory.Pattern(TemplateText, segments);
    }
}
