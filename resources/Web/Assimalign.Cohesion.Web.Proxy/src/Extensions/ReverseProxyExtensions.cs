using System;

using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// Pipeline-builder members that add a reverse proxy to a web application.
/// </summary>
/// <remarks>
/// The routes come from <c>builder.Services.AddReverseProxy(...)</c>, which validates them when the
/// application is composed; <c>UseReverseProxy</c> takes no configuration of its own. See the area's
/// middleware order for where it goes: after <c>UseForwardedHeaders</c> and <c>UseHostFiltering</c>,
/// because it routes on the validated effective host, inside the exception boundary, and ahead of the
/// middleware that would change a relayed response.
/// </remarks>
public static class ReverseProxyExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the reverse proxy to the pipeline. A request one of its routes takes is forwarded to the
        /// route's next destination, and the destination's response, protocol upgrade or failure is
        /// relayed to the client; any other request goes to the next middleware.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The routes are the ones <c>builder.Services.AddReverseProxy(...)</c> registered as the
        /// application's <c>reverse-proxy</c> setting, read on each request, so a route list an operator
        /// surface sets applies from the next request. The route that takes a request is the most
        /// specific: the closest host match, then the longest path prefix, then the first registered.
        /// </para>
        /// <para>
        /// The proxy answers a failure as a gateway does: 502 Bad Gateway when a destination cannot be
        /// reached or fails, 504 Gateway Timeout when it stops making progress, 508 Loop Detected when the
        /// request has passed through this proxy before. A response is relayed as it arrives when the
        /// exchange can stream; otherwise it is buffered up to
        /// <see cref="Http.HttpForwarderOptions.MaxBufferedResponseBodySize"/>.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pipeline is composed, if <c>builder.Services.AddReverseProxy(...)</c> was not
        /// called: a reverse proxy has no safe default route.
        /// </exception>
        public IWebApplicationPipelineBuilder UseReverseProxy()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                ReverseProxySetting setting = ReverseProxySetting.Find(context)
                    ?? throw new InvalidOperationException(ErrorMessages.ReverseProxyNotRegistered);

                ReverseProxyMiddleware middleware = new(setting);
                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
