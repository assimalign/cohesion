using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Routing.Patterns;

[DebuggerDisplay("{Context}")]
public sealed class RoutePatternParameterPolicyReference
{
    internal RoutePatternParameterPolicyReference(string content)
    {
        Content = content;
    }

    internal RoutePatternParameterPolicyReference(IRouteParameterPolicy parameterPolicy)
    {
        ParameterPolicy = parameterPolicy;
    }

    /// <summary>
    /// Gets the constraint text.
    /// </summary>
    public string? Content { get; }

    /// <summary>
    /// Gets a pre-existing <see cref="IParameterPolicy"/> that was used to construct this reference.
    /// </summary>
    public IRouteParameterPolicy? ParameterPolicy { get; }
}
