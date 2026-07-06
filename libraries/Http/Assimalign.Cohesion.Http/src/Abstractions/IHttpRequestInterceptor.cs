using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A server-side interception point applied while a request is being parsed, before the request
/// is dispatched to the application. Interceptors let feature packages participate at
/// request-parse time — adjusting parse-time knobs, attaching typed
/// <see cref="IHttpFeature"/>s, and wrapping the request body stream — without the transport
/// taking a dependency on any feature package.
/// </summary>
/// <remarks>
/// <para>
/// Interceptors are registered on the server transport's listener options and are invoked in
/// registration order. A registered instance is shared across <b>all</b> connections and
/// requests served by the listener: implementations must be stateless and thread-safe, and any
/// per-request state belongs in <see cref="HttpRequestInterceptorContext.Features"/>, never in
/// instance fields.
/// </para>
/// <para>
/// Both members ship default implementations so an interceptor overrides only the points it
/// needs; future interception points are added the same way without breaking existing
/// implementations (see the core design notes on interface evolution via default members).
/// </para>
/// <para>
/// Hooks run inline on the connection's parse path. They must be CPU-only — no I/O, no locks,
/// no blocking waits — because a stalled hook stalls the connection and pins a thread-pool
/// thread. A hook that needs to reject the current request throws
/// <see cref="HttpRequestRejectedException"/>; the transport answers with the carried status
/// code and closes the connection. Any other exception is treated as a programmer error and
/// propagates.
/// </para>
/// </remarks>
public interface IHttpRequestInterceptor
{
    /// <summary>
    /// Called once per request, after the request head (request line / pseudo-headers and header
    /// fields) has been parsed and before the request body is surfaced to the application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The context's header view is read-only; derived or normalized values belong in
    /// <see cref="HttpRequestInterceptorContext.Features"/>. The
    /// <see cref="HttpRequestInterceptorContext.MaxRequestBodySize"/> knob may be adjusted here
    /// while <see cref="HttpRequestInterceptorContext.IsMaxRequestBodySizeReadOnly"/> is
    /// <see langword="false"/>.
    /// </para>
    /// <para>
    /// Timing is protocol-dependent: on HTTP/1.1 the head hook runs before any body octet is
    /// consumed from the wire, so body-size adjustments precede enforcement; HTTP/2 and HTTP/3
    /// currently buffer a stream's body before the head is decoded, so the hook runs before the
    /// body is <em>exposed</em> but not before it was <em>received</em>.
    /// </para>
    /// </remarks>
    /// <param name="context">The parse-time view of the request being read.</param>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown by implementations to reject the request with a 4xx/5xx status.
    /// </exception>
    void OnRequestHead(HttpRequestInterceptorContext context)
    {
    }

    /// <summary>
    /// Called once per request, after the request body stream has been materialized and before
    /// the request is dispatched to the application. Returns the stream the application will
    /// observe — either <paramref name="body"/> unchanged or a wrapping stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interceptors chain in registration order: each receives the stream returned by the
    /// previous one, so the last registered interceptor produces the outermost wrapper. A
    /// wrapper owns the stream it wraps — disposing the outermost stream must dispose the whole
    /// chain, which the framework triggers when the exchange is disposed.
    /// </para>
    /// <para>
    /// The incoming stream is forward-only readable and is not guaranteed to be seekable;
    /// wrappers must not require seeking. The hook also runs for empty bodies (a wrapped view of
    /// an empty body is still meaningful, for example when hashing a representation); it is
    /// skipped for CONNECT tunnels, whose post-head octets are tunnel traffic rather than a
    /// message body.
    /// </para>
    /// </remarks>
    /// <param name="context">The parse-time view of the request being read.</param>
    /// <param name="body">The request body stream produced so far.</param>
    /// <returns>The stream to surface to the application.</returns>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown by implementations to reject the request with a 4xx/5xx status.
    /// </exception>
    Stream OnRequestBody(HttpRequestInterceptorContext context, Stream body)
    {
        return body;
    }
}
