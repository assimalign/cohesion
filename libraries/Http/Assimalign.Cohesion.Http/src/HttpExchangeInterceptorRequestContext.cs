using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// The parse-time view of a request handed to <see cref="IHttpExchangeInterceptor"/>
/// implementations by the server transport.
/// </summary>
/// <remarks>
/// <para>
/// The transport constructs one context per request and keeps it for the whole exchange — not merely
/// until dispatch: the request body freezes and enforces its knobs at its first read, and the response
/// reads <see cref="MinResponseDataRate"/> at its first write. Features that hold a reference to the
/// context (for example a write-through view over <see cref="MaxRequestBodySize"/>) remain valid until
/// the exchange completes.
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
/// Members added in future versions are optional with sensible defaults so existing
/// construction sites (including test fakes) keep compiling; the per-request rate knobs
/// (<see cref="MinRequestBodyDataRate"/>, <see cref="MinResponseDataRate"/>) are such members,
/// settable until the transport freezes them, and <see cref="CanHaveBody"/> is another.
/// </para>
/// </remarks>
public sealed class HttpExchangeInterceptorRequestContext
{
    private long? _maxRequestBodySize;
    private HttpMinDataRate? _minRequestBodyDataRate;
    private HttpMinDataRate? _minResponseDataRate;
    // A singleton array is also the transport's final response list on the common one-added path.
    // Promote to a list only when another distinct interceptor joins the exchange (#1389).
    private IReadOnlyList<IHttpExchangeInterceptor>? _responseInterceptors;

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
    /// Gets the <c>:protocol</c> pseudo-header of an HTTP/2 or HTTP/3 <em>extended CONNECT</em>
    /// (RFC 8441 §4, RFC 9220 §3), such as <c>websocket</c>, or <see langword="null"/> for any other
    /// request, including every HTTP/1.1 request and a classic <c>CONNECT</c>.
    /// </summary>
    /// <remarks>
    /// The transport sets it only after validating the request as an extended CONNECT: the value is
    /// an RFC 9110 token, the method is <c>CONNECT</c>, and the request also carries <c>:scheme</c>, <c>:path</c>
    /// and <c>:authority</c>. A non-<see langword="null"/> value therefore identifies a request that may be
    /// answered with a stream tunnel (<see cref="IHttpExchangeControl.AcceptTunnelAsync"/>), which
    /// <see cref="Method"/> alone cannot tell apart from a classic <c>CONNECT</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">A non-null value is not an RFC 9110 token.</exception>
    public string? Protocol
    {
        get;
        init
        {
            if (value is not null && !HttpFieldName.IsValid(value, out _))
            {
                throw new ArgumentException(ErrorMessages.ExtendedConnectProtocolInvalid, nameof(value));
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets whether reading the request body can still detect a client fault after dispatch: content
    /// may follow the head, or declared content still requires validation. <see langword="false"/> only
    /// when the transport knows there is no body left to read or validate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transport decides it from the request's framing, which only it sees:
    /// </para>
    /// <list type="bullet">
    /// <item><description><b>HTTP/1.1</b> — the framing declares a body: a <c>Transfer-Encoding</c>, or a
    /// <c>Content-Length</c> other than zero (RFC 9112 §6). Without either the request has no body, and a
    /// <c>Content-Length: 0</c> declares an empty one.</description></item>
    /// <item><description><b>HTTP/2</b> — the HEADERS frame that carried the head did not end the stream
    /// (no <c>END_STREAM</c>, RFC 9113 §8.1), whatever <c>content-length</c> it declares: DATA or a trailer
    /// section may still follow, and either can make the request malformed.</description></item>
    /// <item><description><b>HTTP/3</b> — the request stream had not visibly ended with its HEADERS frame
    /// when the request was dispatched (RFC 9114 §4.1), or a positive declared content length still
    /// requires validation. A known end and an empty input otherwise rule out a body, including when
    /// the QUIC driver exposes its observed end before the input pipe reads zero bytes. A late or
    /// unobserved end remains conservative.</description></item>
    /// </list>
    /// <para>
    /// A <c>CONNECT</c> never has one: the octets after its head are tunnel traffic, not content (RFC 9110
    /// §9.3.6). The body may still turn out to be empty. An interceptor that needs an exchange's response
    /// phase only for a request that can fail while its body is read reads this instead of the framing
    /// fields, which an HTTP/2 or HTTP/3 request need not carry.
    /// </para>
    /// <para>
    /// The property is optional: a context constructed outside a transport (a test fake) reports
    /// <see langword="true"/> unless it says otherwise, since a request can have a body until its framing
    /// shows it cannot.
    /// </para>
    /// </remarks>
    public bool CanHaveBody { get; init; } = true;

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
    /// Gets the transport connection metadata for the exchange: the local and remote endpoints, plus
    /// any facet the transport publishes on it, such as the TLS handshake facts (see
    /// <see cref="IHttpConnectionInfo"/>). The server transport passes the instance the exchange's
    /// <see cref="IHttpContext.ConnectionInfo"/> returns.
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
                    ErrorMessages.RequestBodySizeLimitReadOnly);
            }

            if (value is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, ErrorMessages.RequestBodySizeLimitInvalid);
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

    /// <summary>
    /// Gets or sets the minimum rate at which the request body must arrive for this exchange, or
    /// <see langword="null"/> to hold the body to no rate. Seeded by the transport from the listener's
    /// configured rate; interceptors, middleware and endpoints may lower, raise, or disable it until the
    /// transport freezes it at the first read of the request body (see
    /// <see cref="IsMinRequestBodyDataRateReadOnly"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Disabling the rate is for a body that legitimately idles: a client-streaming or duplex call, or
    /// a long-lived upload that sends a message now and then. It removes the slow-body defence for this
    /// request only, so decide it from the endpoint, never from request-supplied values. A
    /// <c>CONNECT</c> tunnel's octets are not a request body, and no rate applies to them whatever this
    /// holds.
    /// </para>
    /// <para>
    /// The property is optional: a context constructed outside a transport (a test fake) starts with
    /// <see langword="null"/>, and every server transport seeds it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when set after the transport has frozen the value
    /// (<see cref="IsMinRequestBodyDataRateReadOnly"/> is <see langword="true"/>).
    /// </exception>
    public HttpMinDataRate? MinRequestBodyDataRate
    {
        get => _minRequestBodyDataRate;
        set
        {
            if (IsMinRequestBodyDataRateReadOnly)
            {
                throw new InvalidOperationException(
                    ErrorMessages.RequestBodyDataRateReadOnly);
            }

            _minRequestBodyDataRate = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="MinRequestBodyDataRate"/> has been frozen by the
    /// transport. The value freezes at the first read of the request body on every protocol, after which
    /// the rate that applies is fixed for the remainder of the exchange.
    /// </summary>
    public bool IsMinRequestBodyDataRateReadOnly { get; private set; }

    /// <summary>
    /// Freezes <see cref="MinRequestBodyDataRate"/> for the remainder of the exchange. Called by the
    /// transport when it starts reading the request body; idempotent.
    /// </summary>
    public void FreezeMinRequestBodyDataRate()
    {
        IsMinRequestBodyDataRateReadOnly = true;
    }

    /// <summary>
    /// Gets or sets the minimum rate at which the peer must accept this exchange's response, or
    /// <see langword="null"/> to hold the response to no rate. Seeded by the transport from the
    /// listener's configured rate; interceptors, middleware and endpoints may lower, raise, or disable it
    /// until the transport freezes it at the first write of the response (see
    /// <see cref="IsMinResponseDataRateReadOnly"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate is the slow-read defence: a peer that withholds flow-control credit (HTTP/2
    /// <c>WINDOW_UPDATE</c>, QUIC <c>MAX_STREAM_DATA</c>/<c>MAX_DATA</c>) or stops reading the connection
    /// is reclaimed once it falls below the rate, and the exchange observes the cancellation. Disabling
    /// it removes that defence for this response only, so decide it from the endpoint, never from
    /// request-supplied values. A tunnel (an accepted <c>CONNECT</c> or protocol upgrade) is not a
    /// response body, and no rate applies to it whatever this holds.
    /// </para>
    /// <para>
    /// The property is optional: a context constructed outside a transport (a test fake) starts with
    /// <see langword="null"/>, and every server transport seeds it.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when set after the transport has frozen the value
    /// (<see cref="IsMinResponseDataRateReadOnly"/> is <see langword="true"/>).
    /// </exception>
    public HttpMinDataRate? MinResponseDataRate
    {
        get => _minResponseDataRate;
        set
        {
            if (IsMinResponseDataRateReadOnly)
            {
                throw new InvalidOperationException(
                    ErrorMessages.ResponseDataRateReadOnly);
            }

            _minResponseDataRate = value;
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="MinResponseDataRate"/> has been frozen by the
    /// transport. The value freezes at the first write of the response on every protocol, after which
    /// the rate that applies is fixed for the remainder of the exchange.
    /// </summary>
    public bool IsMinResponseDataRateReadOnly { get; private set; }

    /// <summary>
    /// Freezes <see cref="MinResponseDataRate"/> for the remainder of the exchange. Called by the
    /// transport when it starts writing the response; idempotent.
    /// </summary>
    public void FreezeMinResponseDataRate()
    {
        IsMinResponseDataRateReadOnly = true;
    }

    /// <summary>
    /// Gets the interceptors a request-parse hook added to this exchange's response phase
    /// (<see cref="AddResponseInterceptor"/>), in the order they were added. Empty when none was.
    /// </summary>
    /// <remarks>
    /// The transport reads this once, when it sets up the exchange after the request-parse hooks
    /// have run.
    /// </remarks>
    public IReadOnlyList<IHttpExchangeInterceptor> ResponseInterceptors
        => _responseInterceptors ?? Array.Empty<IHttpExchangeInterceptor>();

    /// <summary>
    /// Adds <paramref name="interceptor"/> to this exchange's response phase only: its
    /// <see cref="IHttpExchangeInterceptor.BeforeResponse"/>,
    /// <see cref="IHttpExchangeInterceptor.BeforeResponseHeadAsync"/> and
    /// <see cref="IHttpExchangeInterceptor.AfterResponseAsync"/> hooks run for this exchange as
    /// though it had declared <see cref="HttpInterceptorScopes.Response"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The response phase costs every exchange it runs for: the transport constructs the raw response
    /// body sink and the exchange control (<see cref="HttpExchangeInterceptorResponseContext"/>)
    /// before the application handler runs. An interceptor declared
    /// <see cref="HttpInterceptorScopes.Response"/> pays that on every exchange. One that needs the
    /// response phase for some exchanges only, such as an HTTP/1.1 protocol upgrade, declares
    /// <see cref="HttpInterceptorScopes.Request"/> and adds itself from a request-parse hook when the
    /// request asks for it, so every other exchange keeps the fast path.
    /// </para>
    /// <para>
    /// Added interceptors run after the listener's response interceptors, in the order they were
    /// added, for every response hook of the exchange. An interceptor takes part at most once per
    /// exchange: adding one that already takes part (registered with the response scope, or added
    /// before) has no further effect. Only a call from a request-parse hook takes effect; the
    /// transport has set up the exchange by the time the application runs.
    /// </para>
    /// </remarks>
    /// <param name="interceptor">The interceptor whose response hooks run for this exchange.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interceptor"/> is <see langword="null"/>.</exception>
    public void AddResponseInterceptor(IHttpExchangeInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        if (_responseInterceptors is null)
        {
            _responseInterceptors = new[] { interceptor };
            return;
        }

        if (_responseInterceptors is List<IHttpExchangeInterceptor> interceptors)
        {
            if (!interceptors.Contains(interceptor))
            {
                interceptors.Add(interceptor);
            }
            return;
        }

        IHttpExchangeInterceptor first = _responseInterceptors[0];
        if (!EqualityComparer<IHttpExchangeInterceptor>.Default.Equals(first, interceptor))
        {
            _responseInterceptors = new List<IHttpExchangeInterceptor>(2) { first, interceptor };
        }
    }
}
