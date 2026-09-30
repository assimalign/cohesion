using System;
using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Builds an immutable <see cref="IRouter"/> from the routes mapped into it.
/// </summary>
/// <remarks>
/// <para>
/// A router builder is <em>per application</em>: each web application owns its own builder (installed
/// through <see cref="RoutingExtensions"/>' <c>AddRouting</c>/<c>UseRouting</c>) so route tables never
/// leak between applications hosted in the same process. There is intentionally no shared/static
/// builder — that was the cross-application state-leakage defect fixed in issue #789.
/// </para>
/// <para>
/// A builder builds one router. The first <see cref="Build"/> call closes the route table and builds
/// the router; every later call returns that same router, and a route mapped after the first call
/// throws <see cref="InvalidOperationException"/> instead of being silently left out. A web
/// application makes that first call when its request pipeline is built at startup
/// (<c>UseRouting</c>), so an invalid route table fails the application's start. Mapping and building
/// are thread-safe: concurrent first calls build one router, and a route mapped concurrently with the
/// build is either part of the router or rejected.
/// </para>
/// </remarks>
public sealed class RouterBuilder : IRouterBuilder
{
    private readonly List<IRouterRoute> _routes = new();
    private readonly Lock _lock = new();

    // Written under _lock; read without it on the fast path of every Build after the first.
    private IRouter? _router;

    // Set by the first Build before the router is constructed, so a failed build cannot reopen the
    // route table: every later Build retries the same closed table and fails the same way.
    private bool _isClosed;

    /// <summary>
    /// Builds the router from the mapped routes on the first call, and returns that same router on
    /// every later call.
    /// </summary>
    /// <returns>The router built from the mapped routes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The route table is invalid: two routes register the same route name (compared
    /// case-insensitively), or a named route exposes no route pattern. A failed build is not
    /// cached; each call rebuilds the closed table and throws again.
    /// </exception>
    public IRouter Build()
    {
        IRouter? router = Volatile.Read(ref _router);
        if (router is not null)
        {
            return router;
        }

        lock (_lock)
        {
            _isClosed = true;

            router = _router;
            if (router is null)
            {
                router = new Router(_routes);
                Volatile.Write(ref _router, router);
            }

            return router;
        }
    }

    /// <summary>
    /// Adds the specified route to the route table.
    /// </summary>
    /// <param name="route">The route to add.</param>
    /// <returns>The current router builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The router has already been built from this builder, which fixed its route table.
    /// </exception>
    public IRouterBuilder Map(IRouterRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);

        lock (_lock)
        {
            if (_isClosed)
            {
                string description = route.Pattern is { } pattern ? $"The route '{pattern.RawText}'" : "A route";

                throw new InvalidOperationException(
                    $"{description} cannot be mapped: the router has already been built from this builder, and its route table is fixed. " +
                    "A web application builds its router when its request pipeline is built at startup, so map every route before the application starts.");
            }

            _routes.Add(route);
        }

        return this;
    }
}
