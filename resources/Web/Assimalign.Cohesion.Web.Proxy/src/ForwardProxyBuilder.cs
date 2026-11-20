using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Web.Proxy.Internal;

namespace Assimalign.Cohesion.Web.Proxy;

/// <summary>
/// Configures the forward proxy that <c>builder.Services.AddForwardProxy(...)</c> registers and
/// <c>UseForwardProxy()</c> places.
/// </summary>
/// <remarks>
/// The builder registers the application's <c>forward-proxy</c> setting, an
/// <see cref="IWebApplicationSetting{TValue}"/> of <see cref="ForwardProxyRule"/> lists on
/// <see cref="IWebApplicationContext.Settings"/>. Its value is the rule list, which an operator surface
/// may replace while the application runs; the middleware reads it on each request. The setting owns
/// the proxy's connection pools, and the application disposes them when it is disposed.
/// </remarks>
public sealed class ForwardProxyBuilder
{
    /// <summary>
    /// Gets the options the builder registers: the rules, how the proxy forwards and tunnels, and its
    /// connection pools.
    /// </summary>
    public ForwardProxyOptions Options { get; } = new();

    /// <summary>
    /// Builds the application's <c>forward-proxy</c> setting from a snapshot of <see cref="Options"/>,
    /// creating the connection pools it owns.
    /// </summary>
    /// <returns>The setting, whose value is the rule list.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ForwardProxyOptions.ConfigureHandler"/> left a handler following redirects, keeping
    /// cookies, decompressing bodies, connecting through a callback or using a proxy; or
    /// <see cref="Http.HttpTunnelOptions.ConnectCallback"/> of <see cref="ForwardProxyOptions.Tunnel"/> is set.
    /// </exception>
    public IWebApplicationSetting<IReadOnlyList<ForwardProxyRule>> Build()
    {
        return ForwardProxySetting.Create(Options);
    }
}
