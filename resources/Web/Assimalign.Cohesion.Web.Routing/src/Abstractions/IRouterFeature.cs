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
    /// Gets the router built from the mapped routes. The router is built once, on first access, and
    /// reused for the lifetime of the feature.
    /// </summary>
    IRouter Router { get; }

    /// <summary>
    /// Gets the builder into which the application's routes are mapped.
    /// </summary>
    IRouterBuilder Builder { get; }
}
