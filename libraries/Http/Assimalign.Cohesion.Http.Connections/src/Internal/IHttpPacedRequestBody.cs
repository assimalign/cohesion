namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A transport request body that holds the peer to the minimum request-body data rate itself and
/// resolves the rate at its first read, freezing the parse context's rate knob then (#1383): HTTP/2's
/// <see cref="Http2RequestBodyStream"/>. Its body-size cap is frozen by the pipeline after the head
/// hooks, because HTTP/2 enforces the cap on receipt rather than at the reader's pace.
/// </summary>
/// <remarks>
/// The shared <see cref="HttpRequestInterceptorPipeline"/> checks the request's innermost body for this
/// interface after the head hooks. A lazy body (<see cref="IHttpLazyRequestBody"/>, HTTP/3) already
/// resolves both knobs at its first read and is not a paced body. Any other body has the rate knob frozen
/// by the pipeline with the cap, since nothing would enforce a later change.
/// </remarks>
internal interface IHttpPacedRequestBody
{
    /// <summary>
    /// Hands the body the request's parse context. The body freezes
    /// <see cref="HttpExchangeInterceptorRequestContext.MinRequestBodyDataRate"/> at its first read and
    /// holds the peer to the frozen value from then on.
    /// </summary>
    /// <param name="interception">The request's parse context.</param>
    void AttachInterception(HttpExchangeInterceptorRequestContext interception);
}
