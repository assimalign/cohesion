namespace Assimalign.Cohesion.Web.Routing;

using Assimalign.Cohesion.Http;

/// <summary>
/// The per-application routing feature. It owns the application's <see cref="IRouterBuilder"/>
/// (into which routes are mapped at configuration time) and the immutable <see cref="IRouter"/>
/// built from it (used at request time to match).
/// </summary>
/// <remarks>
/// One instance is registered per web application (via <c>AddRouting</c>) and installed on each
/// request's <see cref="IHttpContext.Features"/> collection. Because the builder lives on this
/// per-application feature rather than on a shared static, two applications hosted in the same
/// process keep fully isolated route tables.
/// </remarks>
public interface IRouterFeature : IHttpFeature
{
    /// <summary>
    /// Gets the router built from the mapped routes.
    /// </summary>
    /// <remarks>
    /// The router is built once and reused for the lifetime of the feature. <c>UseRouting</c> builds it
    /// when the application's request pipeline is built at startup, before the first request, so an
    /// invalid route table fails the application's start rather than its requests; an earlier access
    /// builds it then. Building fixes the route table: mapping another route into
    /// <see cref="Builder"/> afterwards throws <see cref="System.InvalidOperationException"/>. Access is
    /// thread-safe.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">
    /// The route table is invalid, for example because two routes register the same route name.
    /// </exception>
    IRouter Router { get; }

    /// <summary>
    /// Gets the builder into which the application's routes are mapped.
    /// </summary>
    /// <remarks>
    /// Routes can be mapped until <see cref="Router"/> is built; afterwards the builder throws
    /// <see cref="System.InvalidOperationException"/>.
    /// </remarks>
    IRouterBuilder Builder { get; }
}
