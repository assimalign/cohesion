using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The outcome of the request-parse interceptor phase run by
/// <see cref="HttpRequestInterceptorPipeline.InterceptAsync"/>: the hook-populated feature collection
/// to flow into the exchange, the effective request-body cap the transport must enforce, and the body
/// the request exposes.
/// </summary>
/// <param name="Features">
/// The feature collection populated by the head hooks, or <see langword="null"/> for the
/// zero-interceptor fast path.
/// </param>
/// <param name="MaxRequestBodySize">
/// The effective per-request body-size cap, in octets, or <see langword="null"/> for unbounded. With
/// interceptors registered this is the parse context's knob as frozen after the head hooks ran;
/// without them it is the registration's configured limit, unchanged. A lazy body
/// (<see cref="IHttpLazyRequestBody"/> — HTTP/3) freezes the knob at its first read instead, so for
/// it this is only the knob's value when the hooks finished, and the body enforces its own.
/// </param>
/// <param name="Body">
/// The body the request exposes: the outermost wrapper the body hooks produced, or the transport
/// body unchanged on the zero-interceptor fast path and for a CONNECT. The transport builds the
/// exchange's request with it (<see cref="TransportHttpRequestHead.Body"/>).
/// </param>
/// <param name="ResponseInterceptors">
/// The response interceptors the hooks added to this exchange alone
/// (<see cref="HttpExchangeInterceptorRequestContext.AddResponseInterceptor"/>), or
/// <see langword="null"/> when none was added; the transport hands them to the exchange
/// (<see cref="TransportHttpContext.AddedResponseInterceptors"/>).
/// </param>
internal readonly record struct HttpRequestInterceptionResult(
    HttpFeatureCollection? Features,
    long? MaxRequestBodySize,
    Stream Body,
    IReadOnlyList<IHttpExchangeInterceptor>? ResponseInterceptors = null);
