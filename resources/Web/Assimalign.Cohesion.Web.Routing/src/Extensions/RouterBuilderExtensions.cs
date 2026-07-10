using System;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Route-group composition extensions for <see cref="IRouterBuilder"/>.
/// </summary>
public static class RouterBuilderExtensions
{
    extension(IRouterBuilder builder)
    {
        /// <summary>
        /// Creates a route group that composes <paramref name="prefix"/>, shared parameter
        /// policies, and shared endpoint metadata onto child routes at registration time.
        /// </summary>
        /// <param name="prefix">
        /// The route-template prefix applied to child routes. May contain parameters (for example
        /// <c>{tenant}/api</c>) and may be empty to share only policies and metadata.
        /// </param>
        /// <returns>A route group builder mapping composed child routes into this router builder.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> or <paramref name="prefix"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="Exceptions.RoutePatternException">
        /// <paramref name="prefix"/> is not a valid route template.
        /// </exception>
        /// <remarks>
        /// Each child route registered through the group is stored as a single fully-composed
        /// route — the router evaluates it exactly like a directly-mapped route, with no
        /// per-request prefix matching. See <see cref="IRouterGroupBuilder"/> for composition,
        /// override, and freeze semantics.
        /// </remarks>
        public IRouterGroupBuilder MapGroup(string prefix)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(prefix);

            return new RouterGroupBuilder(builder, parent: null, prefix);
        }
    }
}
