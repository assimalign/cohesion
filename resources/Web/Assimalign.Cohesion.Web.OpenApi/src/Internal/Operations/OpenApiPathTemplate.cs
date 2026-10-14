using System.Text;

using Assimalign.Cohesion.Web.Routing.Patterns;

namespace Assimalign.Cohesion.Web.OpenApi.Internal;

/// <summary>
/// Writes a route pattern as an OpenAPI path template: <c>orders/{id:long}</c> becomes
/// <c>/orders/{id}</c>.
/// </summary>
/// <remarks>
/// <para>
/// OpenAPI path templating names a parameter in braces and nothing else, so inline constraints,
/// defaults, the optional marker and the catch-all stars are dropped; the schema of the matching path
/// parameter carries what the constraints say. Two consequences are inherent to the format, not chosen
/// here: an optional route parameter is described as a required path parameter (OpenAPI path parameters
/// are always required), and a catch-all parameter's value may contain slashes the template cannot show.
/// </para>
/// <para>
/// Literal text passes through as authored. Routes whose templates differ only in constraints
/// (<c>{id:int}</c> beside <c>{id}</c>) produce the same path; the first route registered keeps it.
/// </para>
/// </remarks>
internal static class OpenApiPathTemplate
{
    /// <summary>
    /// Formats the route pattern as an OpenAPI path.
    /// </summary>
    /// <param name="pattern">The route's composed pattern.</param>
    /// <returns>The path, beginning with <c>/</c>.</returns>
    public static string Format(RoutePattern pattern)
    {
        StringBuilder builder = new();

        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            builder.Append('/');

            foreach (RoutePatternSegment part in segment.Segments)
            {
                switch (part)
                {
                    case RoutePatternLiteralSegment literal:
                        builder.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorSegment separator:
                        builder.Append(separator.Content);
                        break;
                    case RoutePatternParameterSegment parameter:
                        builder.Append('{').Append(parameter.Name).Append('}');
                        break;
                }
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }
}
