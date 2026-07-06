using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The parse-time view of a request handed to <see cref="IHttpRequestInterceptor"/>
/// implementations by the server transport.
/// </summary>
/// <remarks>
/// <para>
/// The transport constructs one context per request and owns it for the duration of the
/// exchange's body consumption — not merely until dispatch. Features that hold a reference to
/// the context (for example a write-through view over <see cref="MaxRequestBodySize"/>) remain
/// valid until the request body has been consumed or the exchange completes.
/// </para>
/// <para>
/// The context is not thread-safe; it must only be touched from the request's parse/dispatch
/// flow. <see cref="Headers"/> is a read-only view — the transport derives framing, keep-alive,
/// and host-resolution decisions from the same underlying collection, so mutation is rejected to
/// keep the parsed message consistent with the wire (a mutable view would be a request-smuggling
/// primitive). Interceptors publish derived values through <see cref="Features"/> instead.
/// </para>
/// <para>
/// All required members are set by the transport at construction; every one is init-only except
/// <see cref="MaxRequestBodySize"/>, whose continued mutability is the point of the knob.
/// Members added in future versions will be optional with sensible defaults so existing
/// construction sites (including test fakes) keep compiling.
/// </para>
/// </remarks>
public sealed class HttpRequestInterceptorContext
{
    private long? _maxRequestBodySize;

    /// <summary>
    /// Gets the HTTP version of the exchange.
    /// </summary>
    public required HttpVersion Version { get; init; }

    /// <summary>
    /// Gets the request method.
    /// </summary>
    public required HttpMethod Method { get; init; }

    /// <summary>
    /// Gets the request path.
    /// </summary>
    public required HttpPath Path { get; init; }

    /// <summary>
    /// Gets the request scheme.
    /// </summary>
    public required HttpScheme Scheme { get; init; }

    /// <summary>
    /// Gets the request host (authority).
    /// </summary>
    public required HttpHost Host { get; init; }

    /// <summary>
    /// Gets a read-only view of the parsed request headers. Mutation throws
    /// <see cref="InvalidOperationException"/>; derived values belong in <see cref="Features"/>.
    /// </summary>
    public required HttpHeaderCollection Headers { get; init; }

    /// <summary>
    /// Gets the feature collection for the exchange. Features attached here are visible to the
    /// application from the first middleware onward, and participate in the exchange's normal
    /// feature-disposal walk.
    /// </summary>
    public required IHttpFeatureCollection Features { get; init; }

    /// <summary>
    /// Gets the transport connection metadata for the exchange (local/remote endpoints).
    /// </summary>
    public required HttpConnectionInfo ConnectionInfo { get; init; }

    /// <summary>
    /// Gets or sets the maximum request body size, in octets, the transport enforces for this
    /// exchange, or <see langword="null"/> for unbounded. Seeded by the transport from the
    /// listener's configured limit; interceptors may adjust it until the transport freezes it
    /// (see <see cref="IsMaxRequestBodySizeReadOnly"/>).
    /// </summary>
    /// <remarks>
    /// Raising the cap (or setting it to <see langword="null"/>) is a hosting-level decision
    /// equivalent to raising the listener-wide limit; never derive a raised cap from
    /// request-supplied values.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when set after the transport has frozen the value
    /// (<see cref="IsMaxRequestBodySizeReadOnly"/> is <see langword="true"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set to a negative value.</exception>
    public required long? MaxRequestBodySize
    {
        get => _maxRequestBodySize;
        set
        {
            if (IsMaxRequestBodySizeReadOnly)
            {
                throw new InvalidOperationException(
                    "The maximum request body size cannot be modified after the transport has started reading the request body.");
            }

            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum request body size must be non-negative or null (unbounded).");
            }

            _maxRequestBodySize = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="MaxRequestBodySize"/> has been frozen by the
    /// transport. The value freezes when the transport starts consuming the request body (at
    /// body materialization for buffered reads; at the first body byte once bodies stream), after
    /// which the effective cap is fixed for the remainder of the exchange.
    /// </summary>
    public bool IsMaxRequestBodySizeReadOnly { get; private set; }

    /// <summary>
    /// Freezes <see cref="MaxRequestBodySize"/> for the remainder of the exchange. Called by the
    /// transport when it starts consuming the request body; idempotent.
    /// </summary>
    public void FreezeMaxRequestBodySize()
    {
        IsMaxRequestBodySizeReadOnly = true;
    }
}
