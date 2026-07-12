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
    private int _maxAutomaticRedirections = 50;

    /// <summary>
    /// Optional base address applied to every <see cref="HttpClient"/> created for this name.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// Gets or sets whether clients created for this name follow <c>3xx</c> redirects
    /// automatically. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This property is the single owner of redirect policy for the name: the factory follows
    /// redirects in its own handler layer — with RFC 10008 &#167; 2.5 method semantics (QUERY is
    /// re-issued with its original content on <c>301</c>/<c>302</c>/<c>307</c>/<c>308</c> and
    /// switched to GET only by <c>303 See Other</c>; the historical POST&#8594;GET rewrite on
    /// <c>301</c>/<c>302</c> is preserved) — and always disables
    /// <see cref="System.Net.Http.SocketsHttpHandler.AllowAutoRedirect"/> on the pooled inner
    /// handler so exactly one layer acts. Set this to <see langword="false"/> to surface raw
    /// <c>3xx</c> responses; configuring redirects through <see cref="ConfigureHandler"/> instead
    /// has no effect.
    /// </para>
    /// <para>
    /// A followed redirect re-serializes the same <see cref="System.Net.Http.HttpContent"/>
    /// instance, so buffered contents (<see cref="System.Net.Http.ByteArrayContent"/>,
    /// <see cref="System.Net.Http.StringContent"/>, &#8230;) ride redirects while one-shot
    /// contents (e.g. <see cref="System.Net.Http.StreamContent"/>) cannot — the same constraint
    /// the BCL's built-in redirect handling carries.
    /// </para>
    /// </remarks>
    public bool AllowAutoRedirect { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of redirects followed per request when
    /// <see cref="AllowAutoRedirect"/> is enabled. Exceeding the cap stops following and returns
    /// the last <c>3xx</c> response. Defaults to 50.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a non-positive value.</exception>
    public int MaxAutomaticRedirections
    {
        get => _maxAutomaticRedirections;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxAutomaticRedirections = value;
        }
    }

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
    /// supply their own handler instance own configuration entirely. Redirect policy is the one
    /// setting this callback cannot own: the factory forces the inner handler's
    /// <see cref="System.Net.Http.SocketsHttpHandler.AllowAutoRedirect"/> off after the callback
    /// runs and follows redirects in its own layer &#8212; configure redirects through
    /// <see cref="AllowAutoRedirect"/> / <see cref="MaxAutomaticRedirections"/> instead.
    /// </remarks>
    public Action<System.Net.Http.SocketsHttpHandler>? ConfigureHandler { get; set; }

    /// <summary>
    /// Optional override that produces the inner <see cref="HttpMessageHandler"/> instead of
    /// the default <see cref="System.Net.Http.SocketsHttpHandler"/>. Tests inject this to
    /// substitute a stub or counting handler; production code rarely needs it.
    /// </summary>
    /// <remarks>
    /// The factory's redirect layer wraps the returned handler while
    /// <see cref="AllowAutoRedirect"/> is enabled; a supplied handler that performs its own
    /// redirect following should disable one or the other so exactly one layer acts.
    /// </remarks>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }
}
