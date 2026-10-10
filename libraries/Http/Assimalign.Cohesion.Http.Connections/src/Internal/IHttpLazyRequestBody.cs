namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A transport request body that is read lazily after the request is dispatched and enforces the
/// per-request body-size cap itself, freezing the parse context's knob at its first read — the
/// HTTP/1.1 model (<see cref="Http1RequestBodyStream"/>), which HTTP/3's
/// <see cref="Http3RequestBodyStream"/> shares.
/// </summary>
/// <remarks>
/// The shared <see cref="HttpRequestInterceptorPipeline"/> checks the request's innermost body for this
/// interface after the head hooks. A lazy body receives the parse context instead of the pipeline
/// freezing the knob, so <see cref="IHttpExchangeInterceptor.BeforeRequestBody"/> hooks, middleware,
/// and endpoints can still raise or lower the cap until the body is first read, and the transport
/// enforces whatever value the knob holds at that moment. A body that is not lazy (HTTP/2's
/// flow-controlled pipe) keeps the pipeline's freeze-after-head behavior.
/// </remarks>
internal interface IHttpLazyRequestBody
{
    /// <summary>
    /// Hands the body the request's parse context. The body freezes
    /// <see cref="HttpExchangeInterceptorRequestContext.MaxRequestBodySize"/> at its first read and
    /// enforces the frozen value from then on.
    /// </summary>
    /// <param name="interception">The request's parse context.</param>
    void AttachInterception(HttpExchangeInterceptorRequestContext interception);
}
