using Assimalign.Cohesion.Http.Internal;
using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Factory-wide configuration for <see cref="HttpClientFactory"/>.
/// </summary>
public sealed class HttpClientFactoryOptions
{
    /// <summary>
    /// Default handler lifetime applied to every named client that does not specify its own
    /// <see cref="NamedHttpClientOptions.HandlerLifetime"/>. After this interval elapses
    /// the factory rotates the underlying <see cref="System.Net.Http.HttpMessageHandler"/>;
    /// the previous handler stays alive for any in-flight clients still holding it and is
    /// disposed once GC reclaims those clients.
    /// </summary>
    /// <remarks>
    /// Two minutes matches the original .NET <c>IHttpClientFactory</c> default. Most
    /// deployments do not need to tune this. Shorter intervals refresh DNS / TLS state more
    /// aggressively at the cost of more handler churn; longer intervals keep more
    /// connections warm at the cost of slower failover when an upstream IP changes.
    /// </remarks>
    public TimeSpan DefaultHandlerLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Optional time source. Tests inject a controllable provider to advance the clock past
    /// <see cref="DefaultHandlerLifetime"/> deterministically; production callers leave this
    /// <see langword="null"/> and the factory falls back to <see cref="TimeProvider.System"/>.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>
    /// Per-name client configuration. Populated by
    /// <see cref="HttpClientFactoryBuilder.AddClient"/>.
    /// </summary>
    public IDictionary<string, NamedHttpClientOptions> NamedClients { get; }
        = new Dictionary<string, NamedHttpClientOptions>(StringComparer.Ordinal);
}
