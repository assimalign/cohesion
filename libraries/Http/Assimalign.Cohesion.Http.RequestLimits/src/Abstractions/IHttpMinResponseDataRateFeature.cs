namespace Assimalign.Cohesion.Http;

/// <summary>
/// A typed <see cref="IHttpFeature"/> exposing, and allowing per-request adjustment of, the minimum
/// rate at which the server transport requires the peer to accept the current exchange's response.
/// </summary>
/// <remarks>
/// <para>
/// The request-limits interceptor (<see cref="HttpRequestLimitsInterceptor.Create"/>) attaches this
/// feature on every request as a write-through view over the parse context's rate knob
/// (<see cref="HttpExchangeInterceptorRequestContext.MinResponseDataRate"/>), the value the transport
/// actually enforces. An endpoint or middleware reads <see cref="MinDataRate"/> to discover the rate,
/// or assigns it to lower, raise or disable it for this response, provided the response has not yet
/// started to be written (<see cref="IsReadOnly"/> is <see langword="false"/>). The transport freezes
/// the value at the first write of the response on HTTP/1.1, HTTP/2 and HTTP/3 alike.
/// </para>
/// <para>
/// The rate is the slow-read defence: a peer that withholds flow-control credit or stops reading is
/// reclaimed once it falls below the rate, its connection or stream released and the exchange
/// cancelled. <see langword="null"/> holds this response to no rate, which suits a response whose
/// reader legitimately pauses for long stretches; it removes the defence for this response only, so
/// decide it from the endpoint, never from request-supplied values. This mirrors Kestrel's
/// <c>IHttpMinResponseDataRateFeature</c>.
/// </para>
/// </remarks>
public interface IHttpMinResponseDataRateFeature : IHttpFeature
{
    /// <summary>
    /// Gets a value indicating whether <see cref="MinDataRate"/> can still be changed. Returns
    /// <see langword="true"/> once the transport has begun writing the response, after which the rate
    /// that applies is fixed for the remainder of the exchange.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets or sets the minimum rate at which the peer must accept the response, or
    /// <see langword="null"/> to hold it to none.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when set after the response has started to be written (<see cref="IsReadOnly"/> is
    /// <see langword="true"/>).
    /// </exception>
    HttpMinDataRate? MinDataRate { get; set; }
}
