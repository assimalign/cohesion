using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Per-name configuration for a client returned by <see cref="IHttpClientFactory.Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// Settings on this options bag fall into two categories:
/// </para>
/// <list type="bullet">
///   <item><description><strong>Per-client</strong> properties applied to every fresh
///   <see cref="HttpClient"/> the factory hands out (<see cref="BaseAddress"/>,
///   <see cref="RequestTimeout"/>, <see cref="ConfigureDefaultHeaders"/>). Mutating these
///   properties on the returned client does not affect other clients.</description></item>
///   <item><description><strong>Per-handler</strong> properties applied once per pooled
///   <see cref="HttpMessageHandler"/> (<see cref="HandlerLifetime"/>,
///   <see cref="ConfigureHandler"/>, <see cref="HandlerFactory"/>). Handlers are shared
///   across every <see cref="HttpClient"/> the factory creates for this name within the
///   lifetime window.</description></item>
/// </list>
/// </remarks>
public sealed class NamedHttpClientOptions
{
    /// <summary>
    /// Optional base address applied to every <see cref="HttpClient"/> created for this name.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Optional request timeout applied to every <see cref="HttpClient"/> created for this
    /// name. When <see langword="null"/>, <see cref="HttpClient"/>'s default
    /// (100&#160;seconds) is used.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>
    /// Optional handler lifetime override for this name. Overrides the factory-wide
    /// <see cref="HttpClientFactoryOptions.DefaultHandlerLifetime"/>. Set to a longer value
    /// for clients hitting stable internal services; set to a shorter value for clients
    /// hitting upstreams whose IPs change frequently.
    /// </summary>
    public TimeSpan? HandlerLifetime { get; set; }

    /// <summary>
    /// Callback invoked once per <see cref="HttpClient"/> instance to apply default headers.
    /// Useful for static auth tokens, user-agent strings, or accept negotiation.
    /// </summary>
    public Action<HttpRequestHeaders>? ConfigureDefaultHeaders { get; set; }

    /// <summary>
    /// Callback invoked once per pooled <see cref="System.Net.Http.SocketsHttpHandler"/> to
    /// tune connection pool sizing, automatic decompression, proxy, certificate validation,
    /// HTTP/2 / HTTP/3 negotiation, and similar handler-level concerns.
    /// </summary>
    /// <remarks>
    /// Ignored when <see cref="HandlerFactory"/> is set &#8211; tests and consumers that
    /// supply their own handler instance own configuration entirely.
    /// </remarks>
    public Action<System.Net.Http.SocketsHttpHandler>? ConfigureHandler { get; set; }

    /// <summary>
    /// Optional override that produces the inner <see cref="HttpMessageHandler"/> instead of
    /// the default <see cref="System.Net.Http.SocketsHttpHandler"/>. Tests inject this to
    /// substitute a stub or counting handler; production code rarely needs it.
    /// </summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }
}
