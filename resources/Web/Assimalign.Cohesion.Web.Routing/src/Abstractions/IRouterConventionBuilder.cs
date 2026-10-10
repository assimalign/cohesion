namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Attaches endpoint metadata to one mapped route (<see cref="IRouterRouteBuilder"/>) or to every
/// route of a route group (<see cref="IRouterGroupBuilder"/>).
/// </summary>
/// <remarks>
/// <para>
/// Metadata attached here is composed into each route when the application's route table is built
/// at startup, not when the call is made. The order of calls therefore does not matter: a group's
/// metadata applies to children mapped before and after it, and a route's metadata can be attached
/// after the route is mapped. The composed collection places outer-group items first, then inner-group
/// items, then the route's own items, so the collection's last-wins
/// <see cref="IRouterRouteMetadataCollection.GetMetadata{TMetadata}"/> resolves the most specific
/// declaration. Attaching metadata after the route table has been built throws
/// <see cref="System.InvalidOperationException"/>, because it could no longer apply.
/// </para>
/// <para>
/// Feature packages add their policy verbs as generic extension members over this interface, so one
/// verb serves routes and groups alike and returns the receiver's own builder type for chaining. For
/// example, <c>RequireRateLimiting</c> appends a rate-limiting policy.
/// </para>
/// </remarks>
public interface IRouterConventionBuilder
{
    /// <summary>
    /// Attaches endpoint metadata items to the route, or to every route of the group.
    /// </summary>
    /// <param name="items">The metadata items. Must not contain <see langword="null"/> entries.</param>
    /// <returns>The current builder, for chaining.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="items"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="items"/> contains a <see langword="null"/> entry.</exception>
    /// <exception cref="System.InvalidOperationException">The route table has already been built.</exception>
    IRouterConventionBuilder WithMetadata(params object[] items);
}
