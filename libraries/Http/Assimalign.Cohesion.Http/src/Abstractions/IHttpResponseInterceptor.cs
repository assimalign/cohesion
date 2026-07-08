using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A server-side interception point applied along the response lifecycle of an exchange.
/// Response interceptors are the symmetric counterpart to <see cref="IHttpRequestInterceptor"/>:
/// they let feature packages participate in the response — attaching typed
/// <see cref="IHttpFeature"/>s, tapping the transport's raw response body sink, mutating the
/// final head as it commits, observing completion, and directing the exchange through
/// <see cref="HttpResponseInterceptorContext.Control"/> — <b>without the transport taking a
/// dependency on any feature package</b>.
/// </summary>
/// <remarks>
/// <para>
/// The hooks follow the response's lifecycle in order: <see cref="BeforeResponse"/> (the exchange
/// is being set up, before the application handler runs), <see cref="BeforeResponseHeadAsync"/>
/// (the final response head is about to be committed to the wire), and
/// <see cref="AfterResponseAsync"/> (the final response has been fully written). Together with
/// the <see cref="IHttpRequestInterceptor"/> hooks and the <see cref="IHttpExchangeControl"/>
/// surface, they are the single generic mechanism by which feature packages tap the connection's
/// handling of an exchange — streaming, Server-Sent Events, interim (<c>1xx</c>) responses,
/// protocol upgrade / <c>CONNECT</c> takeover, and later compression all plug in here rather than
/// through per-capability seams.
/// </para>
/// <para>
/// Interceptors are registered on the server transport's listener options and are invoked in
/// registration order. A registered instance is shared across <b>all</b> connections and requests
/// served by the listener: implementations must be stateless and thread-safe, and any per-request
/// state belongs in <see cref="HttpResponseInterceptorContext.Features"/>, never in instance
/// fields.
/// </para>
/// <para>
/// Every member ships a default implementation so an interceptor overrides only what it needs;
/// future interception points are added the same way without breaking existing implementations.
/// Execution-context constraints differ per hook and are documented on each member:
/// <see cref="BeforeResponse"/> runs inline on the connection's parse/dispatch path (on HTTP/2
/// that is the connection's single frame pump) and must be CPU-only, while the asynchronous hooks
/// run on the exchange's send path where awaiting is safe.
/// </para>
/// </remarks>
public interface IHttpResponseInterceptor
{
    /// <summary>
    /// Called once per exchange, after the request head has been parsed and before the application
    /// handler runs. Implementations attach response features, may wrap the raw response body sink
    /// exposed on the context, and may capture <see cref="HttpResponseInterceptorContext.Control"/>
    /// into a feature for later use; nothing has been written to the wire yet, so the response
    /// status and headers are still fully mutable by the application afterward.
    /// </summary>
    /// <remarks>
    /// Runs inline while the exchange is being set up — on HTTP/2 that is the connection's single
    /// frame pump — so implementations must be CPU-only: no I/O, no locks, no blocking waits.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    void BeforeResponse(HttpResponseInterceptorContext context)
    {
    }

    /// <summary>
    /// Called exactly once per exchange, immediately before the final response head is committed
    /// to the wire — on the buffered path when the transport writes the response, or on the
    /// streaming path when the first body write/flush commits the head. This is the last point at
    /// which the response status and <see cref="HttpResponseInterceptorContext.Headers"/> can be
    /// mutated (content negotiation, compression headers, security headers), an interim
    /// (<c>1xx</c>) response can be emitted, or the exchange can be redirected through
    /// <see cref="HttpResponseInterceptorContext.Control"/> (<see cref="IHttpExchangeControl.Abort"/>
    /// / <see cref="IHttpExchangeControl.TakeOver"/> — the transport re-reads the directive after
    /// the hooks run and honors it instead of writing the head).
    /// </summary>
    /// <remarks>
    /// Runs on the exchange's send path (never the HTTP/2 frame pump), so awaiting is safe. Not
    /// invoked when the exchange was aborted or taken over before the head commit, nor for
    /// transport-generated error responses that never became an exchange.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>A task that completes when the hook's work is done.</returns>
    ValueTask BeforeResponseHeadAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called exactly once per exchange, after the final response has been fully written to the
    /// transport — the buffered response flushed, or the streamed response finalized with its wire
    /// terminator. Implementations observe the completed exchange (access logging, metrics,
    /// digests); the response is already on the wire, so mutations here have no effect on it.
    /// </summary>
    /// <remarks>
    /// Runs on the exchange's send path, so awaiting is safe. Not invoked when the exchange was
    /// aborted or taken over — there is no final response to observe in either case.
    /// </remarks>
    /// <param name="context">The response-lifecycle view of the exchange.</param>
    /// <param name="cancellationToken">A token to cancel the work.</param>
    /// <returns>A task that completes when the hook's work is done.</returns>
    ValueTask AfterResponseAsync(HttpResponseInterceptorContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
