using System;
using System.Globalization;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

/// <summary>
/// A literal part of a route pattern.
/// </summary>
public sealed class RoutePatternLiteralSegment : RoutePatternSegment
{
    /// <summary>
    /// Initializes a literal route part.
    /// </summary>
    /// <param name="content">The nonempty literal text, without a query separator.</param>
    /// <exception cref="ArgumentException">The content is null, empty or contains <c>?</c>.</exception>
    public RoutePatternLiteralSegment(string content)
        : base(RoutePatternSegmentKind.Literal)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        if (content.Contains('?'))
        {
            throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.LiteralRouteSegmentInvalid, content), nameof(content));
        }

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
