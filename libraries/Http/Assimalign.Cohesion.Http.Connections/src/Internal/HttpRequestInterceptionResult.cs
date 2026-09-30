namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The outcome of the request-parse interceptor phase run by
/// <see cref="HttpRequestInterceptorPipeline.InterceptAsync"/>: the hook-populated feature collection
/// to flow into the exchange, and the effective request-body cap the transport must enforce.
/// </summary>
/// <param name="Features">
/// The feature collection populated by the head hooks, or <see langword="null"/> for the
/// zero-interceptor fast path.
/// </param>
/// <param name="MaxRequestBodySize">
/// The effective per-request body-size cap, in octets, or <see langword="null"/> for unbounded. With
/// interceptors registered this is the parse context's knob as frozen after the head hooks ran;
/// without them it is the registration's configured limit, unchanged.
/// </param>
internal readonly record struct HttpRequestInterceptionResult(HttpFeatureCollection? Features, long? MaxRequestBodySize);
