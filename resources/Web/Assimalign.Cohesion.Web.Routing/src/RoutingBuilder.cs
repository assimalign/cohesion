using Assimalign.Cohesion.Web.Routing.Internal;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Composes the application's routing feature: the per-application <see cref="IRouterFeature"/> that
/// <c>UseRouting</c> resolves and the <c>Map</c> verbs map into.
/// </summary>
/// <remarks>
/// <para>
/// Applications register routing with <c>builder.Services.AddRouting()</c>. That verb is a component
/// integration (<c>Properties/ComponentIntegrations.cs</c>, owner decisions 34 and 39): it creates the
/// builder, runs the optional configure callback, calls <see cref="Build"/>, and registers the feature as
/// an <c>IHttpFeature</c> singleton. A composition surface without a service container registers
/// <see cref="Build"/>'s result through <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// Routing has no application-wide settings yet, so there is nothing to configure and the verb is called
/// bare; application-wide routing settings belong here when routing gains them. This builder composes the
/// feature; <see cref="RouterBuilder"/> is the route table the feature owns, which routes are mapped into.
/// </para>
/// </remarks>
public sealed class RoutingBuilder
{
    /// <summary>
    /// Initializes a builder for the application's routing feature.
    /// </summary>
    public RoutingBuilder()
    {
    }

    /// <summary>
    /// Builds a routing feature with an empty route table of its own.
    /// </summary>
    /// <remarks>
    /// Every call returns a new feature with its own <see cref="RouterBuilder"/>, so two applications that
    /// each register routing never share a route table (#789).
    /// </remarks>
    /// <returns>The routing feature, an <c>IHttpFeature</c> for the application to register.</returns>
    public IRouterFeature Build() => new RouterFeature();
}
