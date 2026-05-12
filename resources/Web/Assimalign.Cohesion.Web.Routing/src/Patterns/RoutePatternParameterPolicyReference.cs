using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

using Policies;

[DebuggerDisplay("{Context}")]
public sealed class RoutePatternParameterPolicyReference
{
    internal RoutePatternParameterPolicyReference(string content)
    {
        Content = content;
    }

    internal RoutePatternParameterPolicyReference(RouteParameterPolicy parameterPolicy)
    {
        ParameterPolicy = parameterPolicy;
    }

    /// <summary>
    /// Gets the constraint text.
    /// </summary>
    public string? Content { get; }

    /// <summary>
    /// Gets a pre-existing <see cref="RouteParameterPolicy"/> that was used to construct this reference.
    /// </summary>
    public RouteParameterPolicy? ParameterPolicy { get; }
}
