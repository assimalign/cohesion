using System;

using Assimalign.Cohesion.Web.Routing.Metadata;

namespace Assimalign.Cohesion.Web.Routing;

/// <summary>
/// Routing's own convention verbs for mapped routes and route groups: route names and host
/// constraints.
/// </summary>
/// <remarks>
/// Each verb appends a metadata item through <see cref="IRouterConventionBuilder.WithMetadata"/>, so it
/// composes when the route table is built and follows the same ordering rules as any other metadata
/// (see <see cref="IRouterConventionBuilder"/>). Feature packages ship their verbs the same way.
/// </remarks>
public static class RouterConventionBuilderExtensions
{
    extension(IRouterRouteBuilder builder)
    {
        /// <summary>
        /// Names the route for URL generation (<see cref="ILinkGenerator"/>) by attaching a
        /// <see cref="RouteNameMetadata"/>.
        /// </summary>
        /// <param name="routeName">The route name. Unique per router, compared case-insensitively.</param>
        /// <returns>The route builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="routeName"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="routeName"/> is empty.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        /// <remarks>A duplicate name fails when the route table is built, at startup.</remarks>
        public IRouterRouteBuilder WithName(string routeName)
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.WithMetadata(new RouteNameMetadata(routeName));
        }
    }

    extension<TBuilder>(TBuilder builder) where TBuilder : IRouterConventionBuilder
    {
        /// <summary>
        /// Restricts the route, or every route of the group, to requests whose host matches one of
        /// <paramref name="hosts"/> by attaching a <see cref="RouteHostMetadata"/>.
        /// </summary>
        /// <param name="hosts">
        /// The accepted hosts: exact hosts, <c>*.wildcard</c> subdomains, <c>host:port</c>, or bracketed
        /// IPv6 literals (see <see cref="RouteHostConstraint"/>).
        /// </param>
        /// <returns>The same builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="hosts"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="hosts"/> contains a <see langword="null"/> entry.</exception>
        /// <exception cref="Exceptions.RoutePatternException">A host is not a well-formed host constraint.</exception>
        /// <exception cref="InvalidOperationException">The route table has already been built.</exception>
        /// <remarks>
        /// <para>
        /// A route-level declaration replaces a group-level one rather than merging with it, because
        /// the router reads the last <see cref="RouteHostMetadata"/> in the route's metadata.
        /// </para>
        /// <para>
        /// The router matches the effective host (<c>context.EffectiveHost</c>): behind a trusted proxy,
        /// the host the client addressed, which <c>UseForwardedHeaders</c> resolves; otherwise the wire
        /// host. Register <c>UseForwardedHeaders</c> ahead of <c>UseRouting</c> when a proxy rewrites
        /// <c>Host</c>, or the routes match the name the proxy dialed.
        /// </para>
        /// <para>
        /// <c>RequireHost</c> selects on a host the client asserts, both on the wire and when a proxy
        /// forwards it, so it is not an access control: any client can send any host the application
        /// admits. Protect internal endpoints with <c>RequireAuthorization</c>, or with a check on the
        /// connection's local endpoint
        /// (<see cref="Assimalign.Cohesion.Http.IHttpConnectionInfo.LocalPort"/>). A port in a
        /// constraint is likewise compared with the port the client asserted, which proxies often
        /// drop, so a port-constrained route cannot partition traffic by listener.
        /// </para>
        /// </remarks>
        public TBuilder RequireHost(params string[] hosts)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.WithMetadata(new RouteHostMetadata(hosts));
            return builder;
        }
    }
}
