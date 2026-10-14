namespace Assimalign.Cohesion.Http;

/// <summary>
/// A typed <see cref="IHttpFeature"/> exposing, and allowing per-request adjustment of, the minimum
/// rate at which the server transport requires the current exchange's request body to arrive.
/// </summary>
/// <remarks>
/// <para>
/// The request-limits interceptor (<see cref="HttpRequestLimitsInterceptor.Create"/>) attaches this
/// feature on every request as a write-through view over the parse context's rate knob
/// (<see cref="HttpExchangeInterceptorRequestContext.MinRequestBodyDataRate"/>), the value the transport
/// actually enforces. An endpoint or middleware reads <see cref="MinDataRate"/> to discover the rate,
/// or assigns it to lower, raise or disable it for this request, provided the request body has not yet
/// begun to be read (<see cref="IsReadOnly"/> is <see langword="false"/>). The transport freezes the
/// value at the first read of the body on HTTP/1.1, HTTP/2 and HTTP/3 alike.
/// </para>
/// <para>
/// <see langword="null"/> holds the request body to no rate. That is the setting for a body that
/// legitimately idles — a client-streaming or duplex call, a long-lived upload that sends a message
/// now and then — which the listener-wide rate would otherwise answer with <c>408 Request Timeout</c>
/// once an idle gap outlasts its allowance. It removes the slow-body defence for this request only,
/// so decide it from the endpoint, never from request-supplied values. This mirrors Kestrel's
/// <c>IHttpMinRequestBodyDataRateFeature</c>, which gRPC sets to <see langword="null"/> on streaming
/// calls.
/// </para>
/// </remarks>
public interface IHttpMinRequestBodyDataRateFeature : IHttpFeature
{
    /// <summary>
    /// Gets a value indicating whether <see cref="MinDataRate"/> can still be changed. Returns
    /// <see langword="true"/> once the transport has begun reading the request body, after which the
    /// rate that applies is fixed for the remainder of the exchange.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets or sets the minimum rate at which the request body must arrive for this exchange, or
    /// <see langword="null"/> to hold it to none.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when set after the request body has started to be read (<see cref="IsReadOnly"/> is
    /// <see langword="true"/>).
    /// </exception>
    HttpMinDataRate? MinDataRate { get; set; }
}
