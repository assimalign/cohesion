using System.Diagnostics;

using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.Routing;


[DebuggerDisplay("{DebuggerToString()}")]
public abstract class RoutePatternSegment
{
    public RoutePatternSegment(RoutePatternSegmentKind kind)
    {
        Kind = kind;
    }

    public RoutePatternSegmentKind Kind { get; }
    public bool IsLiteral => Kind == RoutePatternSegmentKind.Literal;
    public bool IsSeparator => Kind == RoutePatternSegmentKind.Separator;
    public bool IsParameter => Kind == RoutePatternSegmentKind.Parameter;
    internal virtual string DebuggerToString()
    {
        return Kind.ToString();
    }
}