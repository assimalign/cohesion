namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Configures one mapped route. Every <c>Map</c> overload that maps a route from a template returns it
/// (raw, source-generated and group <c>Map*</c> alike), so per-route policies attach where the route is
/// mapped: <c>app.MapGet("/orders/{id:int}", handler).WithName("order").RequireRateLimiting("api")</c>.
/// </summary>
/// <remarks>
/// Metadata attached through this builder is composed into the route when the route table is built,
/// after the metadata of every group the route belongs to (see <see cref="IRouterConventionBuilder"/>).
/// </remarks>
public interface IRouterRouteBuilder : IRouterConventionBuilder
{
    /// <summary>
    /// Attaches endpoint metadata items to the route.
    /// </summary>
    /// <param name="items">The metadata items. Must not contain <see langword="null"/> entries.</param>
    /// <returns>The current route builder, for chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    /// <exception cref="System.InvalidOperationException">The route table has already been built.</exception>
    new IRouterRouteBuilder WithMetadata(params object[] items);
}
