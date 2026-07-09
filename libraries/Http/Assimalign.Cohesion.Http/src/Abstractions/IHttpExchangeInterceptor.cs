using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The server transport's <b>single</b> extension seam for participating in an HTTP exchange's
/// lifecycle. One registered interceptor can tap every lifecycle point — the request-parse hooks
/// and the response hooks — so a feature is one logical unit with one registration, rather than a
/// pair of per-phase registrations.
/// </summary>
/// <remarks>
/// <para>
/// The hooks follow the exchange's lifecycle in order. Request phase (parse path):
/// <see cref="AfterRequestHead"/> → the body-size knob freezes → <see cref="BeforeRequestBody"/> →
/// <see cref="AfterRequestBody"/>. Response phase: <see cref="BeforeResponse"/> (exchange setup,
/// before the application handler) → <see cref="BeforeResponseHeadAsync"/> (the final head is
/// about to commit) → <see cref="AfterResponseAsync"/> (the final response is fully written).
/// <see cref="Scopes"/> declares which phases the interceptor participates in, so the transport
/// invokes it only where it is needed and its zero-cost fast paths are preserved exactly.
/// </para>
/// <para>
/// <b>Implement by deriving from <see cref="HttpExchangeInterceptor"/></b> — the guided abstract
/// base whose virtual no-op members let an implementation override only the hooks it needs. The
/// base is this seam's compatibility surface: future lifecycle hooks are added there as virtual
/// no-ops, so implementations built on it keep compiling. Implementing this interface directly is
/// permitted (it is the contract the transport consumes) but opts out of that guarantee: every
/// member must be implemented, including hooks added later.
/// </para>
/// <para>
/// Execution-context constraints differ per hook and are part of the contract. The parse-path
/// hooks (<see cref="AfterRequestHead"/>, <see cref="BeforeRequestBody"/>,
/// <see cref="AfterRequestBody"/>, <see cref="BeforeResponse"/>) run inline on the connection's
/// parse/dispatch path — on HTTP/2 that is the connection's single frame pump, where a stalled
/// hook stalls every multiplexed stream on the connection — and must be CPU-only: no I/O, no
/// locks, no blocking waits. The send-path hooks (<see cref="BeforeResponseHeadAsync"/>,
/// <see cref="AfterResponseAsync"/>) run on the exchange's send path where awaiting is safe; the
/// asymmetric signatures encode exactly that constraint.
/// </para>
/// <para>
/// Interceptors are registered on the server transport's listener options and are invoked in
/// registration order (one ordering across the whole lifecycle). A registered instance is shared
/// across <b>all</b> connections and requests served by the listener: implementations must be
/// stateless and thread-safe, and any per-request state belongs in the exchange's feature
/// collection, never in instance fields.
/// </para>
/// <para>
/// Layering: interceptors are wiring, not decision makers. They attach typed
/// <see cref="IHttpFeature"/>s, transform parse/emit-time state, and wrap the transport's
/// mechanisms (<see cref="HttpResponseInterceptorContext.ResponseBody"/>,
/// <see cref="HttpResponseInterceptorContext.Control"/>) into application-facing features — the
/// application then makes the decisions through those features and through
/// <see cref="IHttpContext"/> (aborting an exchange is <see cref="IHttpContext.Cancel"/>, an
/// application-layer act; there is no abort on this seam). A request-parse hook that must refuse a
/// request throws <see cref="HttpRequestRejectedException"/>; any other exception is a programmer
/// error and propagates.
/// </para>
/// </remarks>
public interface IHttpExchangeInterceptor
{
    /// <summary>
    /// Gets the phases this interceptor participates in. The transport reads this once, at
    /// snapshot time; it is not consulted again, so the value must be constant for the
    /// interceptor's lifetime.
    /// </summary>
    HttpInterceptorScopes Scopes { get; }

    /// <summary>
    /// Called once per request, after the request head (request line / pseudo-headers and header
    /// fields) has been parsed and before the request body is surfaced to the application.
    /// Requires <see cref="HttpInterceptorScopes.Request"/>.
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
    void AfterRequestHead(HttpRequestInterceptorContext context);

    /// <summary>
    /// Called once per request, after every head hook has run and the effective body-size cap has
    /// been frozen, immediately before the transport reads (HTTP/1.1) or exposes (HTTP/2 / HTTP/3)
    /// the request body. On HTTP/1.1 this precedes the automatic <c>Expect: 100-continue</c>
    /// solicitation, so a hook that rejects here does so before the body is solicited from the
    /// peer. Skipped for CONNECT tunnels, whose post-head octets are tunnel traffic rather than a
    /// message body. Requires <see cref="HttpInterceptorScopes.Request"/>.
    /// </summary>
    /// <param name="context">The parse-time view of the request being read.</param>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown by implementations to reject the request with a 4xx/5xx status.
    /// </exception>
    void BeforeRequestBody(HttpRequestInterceptorContext context);

    /// <summary>
    /// Called once per request, after the request body stream has been materialized and before
    /// the request is dispatched to the application. Returns the stream the application will
    /// observe — either <paramref name="body"/> unchanged or a wrapping stream. Requires
    /// <see cref="HttpInterceptorScopes.Request"/>.
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
    /// skipped for CONNECT tunnels.
    /// </para>
    /// </remarks>
    /// <param name="context">The parse-time view of the request being read.</param>
    /// <param name="body">The request body stream produced so far.</param>
    /// <returns>The stream to surface to the application.</returns>
    /// <exception cref="HttpRequestRejectedException">
    /// Thrown by implementations to reject the request with a 4xx/5xx status.
    /// </exception>
    Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body);

    /// <summary>
    /// Called once per exchange, after the request head has been parsed and before the application
    /// handler runs. Implementations attach response features, may wrap the raw response body sink
    /// exposed on the context, and may capture <see cref="HttpResponseInterceptorContext.Control"/>
    /// into a feature for later use; nothing has been written to the wire yet, so the response
    /// status and headers are still fully mutable by the application afterward. Requires
    /// <see cref="HttpInterceptorScopes.Response"/>.
    /// </summary>
    /// <remarks>
    /// Runs inline while the exchange is being set up — on HTTP/2 that is the connection's single
    /// frame pump — so implementations must be CPU-only: no I/O, no locks, no blocking waits.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    void BeforeResponse(HttpResponseInterceptorContext context);

    /// <summary>
    /// Called exactly once per exchange, immediately before the final response head is committed
    /// to the wire — on the buffered path when the transport writes the response, or on the
    /// streaming path when the first body write/flush commits the head. This is the last point at
    /// which the response status and <see cref="HttpResponseInterceptorContext.Headers"/> can be
    /// mutated (content negotiation, compression headers, security headers) or an interim
    /// (<c>1xx</c>) response emitted through <see cref="HttpResponseInterceptorContext.Control"/>.
    /// Requires <see cref="HttpInterceptorScopes.Response"/>.
    /// </summary>
    /// <remarks>
    /// Runs on the exchange's send path (never the HTTP/2 frame pump), so awaiting is safe. Not
    /// invoked when the exchange was aborted (<see cref="IHttpContext.Cancel"/>) or its connection
    /// taken over before the head commit, nor for transport-generated error responses that never
    /// became an exchange; the transport re-reads the exchange's state after the hooks run, so a
    /// concurrent abort or a hook-driven takeover is honored instead of writing the head.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>A task that completes when the hook's work is done.</returns>
    ValueTask BeforeResponseHeadAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Called exactly once per exchange, after the final response has been fully written to the
    /// transport — the buffered response flushed, or the streamed response finalized with its wire
    /// terminator. Implementations observe the completed exchange (access logging, metrics,
    /// digests); the response is already on the wire, so mutations here have no effect on it.
    /// Requires <see cref="HttpInterceptorScopes.Response"/>.
    /// </summary>
    /// <remarks>
    /// Runs on the exchange's send path, so awaiting is safe. Not invoked when the exchange was
    /// aborted or taken over — there is no final response to observe in either case.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>A task that completes when the hook's work is done.</returns>
    ValueTask AfterResponseAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken);
}
