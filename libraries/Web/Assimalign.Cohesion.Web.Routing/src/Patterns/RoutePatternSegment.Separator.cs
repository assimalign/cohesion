using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

public sealed class RoutePatternSeparatorSegment : RoutePatternSegment
{
    public RoutePatternSeparatorSegment(string content) : base(RoutePatternSegmentKind.Separator)
    {
        Debug.Assert(!string.IsNullOrEmpty(content));

        Content = content;
    }

    /// <summary>
    /// Gets the text content of the part.
    /// </summary>
    public string Content { get; }

    internal override string DebuggerToString()
    {
        return Content;
    }
}
