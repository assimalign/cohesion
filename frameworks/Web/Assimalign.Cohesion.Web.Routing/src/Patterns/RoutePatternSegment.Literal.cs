using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

public sealed class RoutePatternLiteralSegment : RoutePatternSegment
{
    public RoutePatternLiteralSegment(string content)
        : base(RoutePatternSegmentKind.Literal)
    {
        Debug.Assert(!string.IsNullOrEmpty(content));
        Content = content;
    }

    /// <summary>
    /// Gets the text content.
    /// </summary>
    public string Content { get; }

    internal override string DebuggerToString()
    {
        return Content;
    }
}
