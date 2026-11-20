using System;

using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// Pipeline-builder members that add a forward proxy to a web application.
/// </summary>
/// <remarks>
/// The rules come from <c>builder.Services.AddForwardProxy(...)</c>, which validates them when the
/// application is composed; <c>UseForwardProxy</c> takes no configuration of its own. The middleware is
/// terminal, so it goes in a branch of its own, such as
/// <c>UseWhen(context =&gt; context.ConnectionInfo.LocalPort == 3128, proxy =&gt; proxy.UseForwardProxy())</c>,
/// or in an application that serves nothing else, right after <c>UseHttpLogging</c> and ahead of every
/// middleware that changes a response.
/// </remarks>
public static class ForwardProxyExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the forward proxy to the pipeline. Every request that reaches it is a proxy request: a
        /// <c>CONNECT</c> opens a tunnel to its authority, and any other method is forwarded to its
        /// absolute-form target, when the proxy's rules allow the target's host and port.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rules are the ones <c>builder.Services.AddForwardProxy(...)</c> registered as the
        /// application's <c>forward-proxy</c> setting, read on each request, so a rule list an operator
        /// surface sets applies from the next request. A target no rule allows is answered with 403
        /// Forbidden. A destination allowed only through a wildcard pattern may not resolve to a loopback,
        /// link-local or unspecified address: a literal one is answered with 403, and a name that resolves
        /// only to such addresses with 502 Bad Gateway, since the proxy never connects to it.
        /// </para>
        /// <para>
        /// The middleware never calls the next one. The transport does not yet say whether a request used
        /// the absolute form (#1895), so an origin-form request that reaches it is forwarded to its
        /// <c>Host</c>, on the same rules; serve the proxy on a port or in a branch of its own.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pipeline is composed, if <c>builder.Services.AddForwardProxy(...)</c> was not
        /// called: a forward proxy has no safe default rule.
        /// </exception>
        public IWebApplicationPipelineBuilder UseForwardProxy()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                ForwardProxySetting setting = ForwardProxySetting.Find(context)
                    ?? throw new InvalidOperationException(ErrorMessages.ForwardProxyNotRegistered);

                ForwardProxyMiddleware middleware = new(setting);
                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
