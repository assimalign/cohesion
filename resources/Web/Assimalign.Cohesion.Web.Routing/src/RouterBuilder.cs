using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Builds immutable router instances from mapped routes.
/// </summary>
public sealed class RouterBuilder : IRouterBuilder
{
    private readonly List<IRouterRoute> _routes = new();



    public static IRouterBuilder Shared { get; } = new RouterBuilder();



    /// <inheritdoc />
    public IRouter Build()
    {
        return new Router(_routes);
    }

    /// <inheritdoc />
    public IRouterBuilder Map(IRouterRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        _routes.Add(route);
        return this;
    }
}
