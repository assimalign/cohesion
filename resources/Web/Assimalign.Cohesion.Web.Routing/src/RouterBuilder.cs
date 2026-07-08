using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Builds an immutable <see cref="IRouter"/> from the routes mapped into it.
/// </summary>
/// <remarks>
/// A router builder is <em>per application</em>: each web application owns its own builder (installed
/// through <see cref="RoutingExtensions"/>' <c>AddRouting</c>/<c>UseRouting</c>) so route tables never
/// leak between applications hosted in the same process. There is intentionally no shared/static
/// builder — that was the cross-application state-leakage defect fixed in issue #789.
/// </remarks>
public sealed class RouterBuilder : IRouterBuilder
{
    private readonly List<IRouterRoute> _routes = new();

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
