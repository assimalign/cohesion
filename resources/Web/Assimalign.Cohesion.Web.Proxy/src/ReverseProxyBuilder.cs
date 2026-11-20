using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// Configures the reverse proxy that <c>builder.Services.AddReverseProxy(...)</c> registers and
/// <c>UseReverseProxy()</c> places.
/// </summary>
/// <remarks>
/// The builder registers the application's <c>reverse-proxy</c> setting, an
/// <see cref="IWebApplicationSetting{TValue}"/> of <see cref="ReverseProxyRoute"/> lists on
/// <see cref="IWebApplicationContext.Settings"/>. Its value is the route list, which an operator
/// surface may replace while the application runs; the middleware reads it on each request. The
/// setting owns the proxy's connection pool, and the application disposes it when it is disposed.
/// </remarks>
public sealed class ReverseProxyBuilder
{
    /// <summary>
    /// Gets the options the builder registers: the routes, how the proxy forwards, and its connection
    /// pool.
    /// </summary>
    public ReverseProxyOptions Options { get; } = new();

    /// <summary>
    /// Builds the application's <c>reverse-proxy</c> setting from a snapshot of <see cref="Options"/>,
    /// creating the connection pool it owns.
    /// </summary>
    /// <returns>The setting, whose value is the route list.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ReverseProxyOptions.ConfigureHandler"/> left the handler following redirects, keeping
    /// cookies or decompressing bodies.
    /// </exception>
    public IWebApplicationSetting<IReadOnlyList<ReverseProxyRoute>> Build()
    {
        return ReverseProxySetting.Create(Options);
    }
}
