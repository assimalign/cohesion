using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The response-setup view of an exchange handed to <see cref="IHttpResponseInterceptor"/>
/// implementations by the server transport, before the application handler runs.
/// </summary>
/// <remarks>
/// <para>
/// The transport constructs one context per exchange and owns it for the exchange's lifetime.
/// Features attached to <see cref="Features"/> are visible to the application from the first
/// middleware onward and participate in the exchange's normal feature-disposal walk.
/// </para>
/// <para>
/// The context is not thread-safe; it must only be touched from the exchange's setup/dispatch
/// flow. All required members are set by the transport at construction. Members added in future
/// versions will be optional with sensible defaults so existing construction sites (including test
/// fakes) keep compiling.
/// </para>
/// </remarks>
public sealed class HttpResponseInterceptorContext
{
    /// <summary>
    /// Gets the HTTP version of the exchange.
    /// </summary>
    public required HttpVersion Version { get; init; }

    /// <summary>
    /// Gets the response header collection. Mutable — an interceptor may set default response
    /// headers here (they are committed to the wire when the response starts). Once the response
    /// has started the headers are locked, so interceptor edits made during
    /// <see cref="IHttpResponseInterceptor.OnResponse"/> always precede the commit.
    /// </summary>
    public required HttpHeaderCollection Headers { get; init; }

    /// <summary>
    /// Gets the feature collection for the exchange. Interceptors install the response feature they
    /// provide here (for example an incremental streaming writer that wraps
    /// <see cref="ResponseBody"/>).
    /// </summary>
    public required IHttpFeatureCollection Features { get; init; }

    /// <summary>
    /// Gets the transport connection metadata for the exchange (local/remote endpoints).
    /// </summary>
    public required HttpConnectionInfo ConnectionInfo { get; init; }

    /// <summary>
    /// Gets the transport's raw response body sink — a write-only stream that frames each write for
    /// the negotiated protocol (chunked transfer coding for HTTP/1.1, <c>DATA</c> frames with
    /// flow-control backpressure for HTTP/2 / HTTP/3), commits the response head on the first write
    /// or flush, and is finalized by the transport when the exchange completes.
    /// </summary>
    /// <remarks>
    /// This is the seam a response feature taps: writing to it (or a wrapper over it) streams the
    /// response body incrementally. It is a generic <see cref="Stream"/> so the protocol core and
    /// feature packages carry no per-protocol framing knowledge. When no interceptor writes to it
    /// the transport falls back to the buffered response path.
    /// </remarks>
    public required Stream ResponseBody { get; init; }
}
