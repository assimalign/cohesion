namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Collects the routes of one route table and builds the <see cref="IRouter"/> that evaluates them.
/// </summary>
public interface IRouterBuilder
{
    /// <summary>
    /// Adds the specified route to the router builder.
    /// </summary>
    /// <param name="route">The route to add to the router builder. Cannot be null.</param>
    /// <returns>The current instance of the router builder with the specified route added.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="route"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// The router has already been built from this builder, which fixed its route table.
    /// </exception>
    IRouterBuilder Map(IRouterRoute route);

    /// <summary>
    /// Builds the router that evaluates the mapped routes.
    /// </summary>
    /// <returns>The router built from the mapped routes.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// The route table is invalid, for example because two routes register the same route name.
    /// </exception>
    /// <remarks>
    /// <see cref="RouterBuilder"/> builds one router: the first call closes the route table, later
    /// calls return the same router, and mapping a route afterwards throws.
    /// </remarks>
    IRouter Build();
}
