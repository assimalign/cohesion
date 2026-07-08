using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Internal.Http1;
using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Internal.Http3;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Configures the connection listeners an <see cref="HttpConnectionListener"/> accepts
/// transport connections from.
/// </summary>
/// <remarks>
/// <para>
/// Each registration binds one HTTP protocol to one listener. Stream protocols (HTTP/1.1,
/// HTTP/2) take an <see cref="IConnectionListener"/> whose capabilities must report a reliable,
/// ordered byte stream; HTTP/3 takes an <see cref="IMultiplexedConnectionListener"/>, where the
/// listener type itself is the shape gate.
/// </para>
/// <para>
/// Version-specific configuration (limits, QPACK) is captured per registration through the
/// <c>Use*</c> overloads that accept a configure callback
/// (<see cref="Http1ConnectionListenerOptions"/>, <see cref="Http2ConnectionListenerOptions"/>,
/// <see cref="Http3ConnectionListenerOptions"/>); the overloads without a callback register with
/// conservative defaults. Cross-version concerns — the request/response interceptors and the
/// accept backlog — remain listener-wide on this type.
/// </para>
/// <para>
/// TLS is not configured here: compose it onto the listener before registration (for example via
/// the security library's <c>UseTls</c> layer). Whether accepted connections are secure is derived
/// from the listener's <see cref="ConnectionCapabilities.Security"/>.
/// </para>
/// </remarks>
public sealed class HttpConnectionListenerOptions
{
    private int _backlogCapacity = 512;

    internal List<HttpListenerRegistration> Registrations { get; } = new List<HttpListenerRegistration>();

    /// <summary>
    /// Gets the ordered list of request-parse interceptors invoked while each request is being
    /// read, before it is dispatched to the application. See
    /// <see cref="IHttpRequestInterceptor"/> for the hook contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is snapshotted when the <see cref="HttpConnectionListener"/> is constructed;
    /// mutations after that point have no effect on the listener. A registered instance is shared
    /// across every connection and request the listener serves, so implementations must be
    /// stateless and thread-safe — per-request state belongs in the exchange's feature collection.
    /// </para>
    /// <para>
    /// When the list is empty the transport takes a fast path with no per-request interception
    /// state allocated at all.
    /// </para>
    /// </remarks>
    public IList<IHttpRequestInterceptor> RequestInterceptors { get; } = new List<IHttpRequestInterceptor>();

    /// <summary>
    /// Gets the ordered list of response interceptors invoked while each exchange's response
    /// pipeline is being set up, before the application handler runs. See
    /// <see cref="IHttpResponseInterceptor"/> for the hook contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the symmetric counterpart to <see cref="RequestInterceptors"/>: response-side feature
    /// packages (incremental streaming, Server-Sent Events, later compression) plug in here so the
    /// transport can expose its raw response body sink to them without depending on the feature
    /// package. The list is snapshotted when the <see cref="HttpConnectionListener"/> is constructed;
    /// mutations after that point have no effect. A registered instance is shared across every
    /// connection and request the listener serves, so implementations must be stateless and
    /// thread-safe.
    /// </para>
    /// <para>
    /// When the list is empty the transport takes the buffered fast path with no per-exchange
    /// response-sink allocation at all.
    /// </para>
    /// </remarks>
    public IList<IHttpResponseInterceptor> ResponseInterceptors { get; } = new List<IHttpResponseInterceptor>();

    /// <summary>
    /// Gets or sets the maximum number of accepted HTTP connections that may be buffered
    /// before producers wait for <see cref="HttpConnectionListener.AcceptOrListenAsync(System.Threading.CancellationToken)"/>
    /// to dequeue them.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the assigned value is less than <c>1</c>.
    /// </exception>
    public int BacklogCapacity
    {
        get => _backlogCapacity;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _backlogCapacity = value;
        }
    }

    /// <summary>
    /// Serves HTTP/1.1 over the supplied connection listener, with default configuration.
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp1(IConnectionListener listener)
    {
        return UseHttp1(listener, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/1.1 over the supplied connection listener, configured through
    /// <paramref name="configure"/> (for example, to tune the registration's
    /// <see cref="Http1ConnectionListenerOptions.Limits"/>).
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <param name="configure">Configures the HTTP/1.1-specific options (see <see cref="Http1ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listener"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp1(IConnectionListener listener, Action<Http1ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Http1ConnectionListenerOptions http1Options = new();
        configure(http1Options);

        return UseStreamListener(HttpProtocol.Http11, listener, (interceptors, responseInterceptors) => new Http1ConnectionFactory(http1Options.Limits, interceptors, responseInterceptors));
    }

    /// <summary>
    /// Serves HTTP/1.1 over the connection listener produced by the supplied factory, with
    /// default configuration.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp1(Func<IConnectionListener> listenerFactory)
    {
        return UseHttp1(listenerFactory, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/1.1 over the connection listener produced by the supplied factory, configured
    /// through <paramref name="configure"/> (for example, to tune the registration's
    /// <see cref="Http1ConnectionListenerOptions.Limits"/>).
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <param name="configure">Configures the HTTP/1.1-specific options (see <see cref="Http1ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listenerFactory"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp1(Func<IConnectionListener> listenerFactory, Action<Http1ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Http1ConnectionListenerOptions http1Options = new();
        configure(http1Options);

        return UseStreamListener(HttpProtocol.Http11, listenerFactory, (interceptors, responseInterceptors) => new Http1ConnectionFactory(http1Options.Limits, interceptors, responseInterceptors));
    }

    /// <summary>
    /// Serves HTTP/2 over the supplied connection listener, with default configuration.
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp2(IConnectionListener listener)
    {
        return UseHttp2(listener, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/2 over the supplied connection listener, configured through
    /// <paramref name="configure"/> (for example, to tune the registration's
    /// <see cref="Http2ConnectionListenerOptions.Limits"/>).
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <param name="configure">Configures the HTTP/2-specific options (see <see cref="Http2ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listener"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp2(IConnectionListener listener, Action<Http2ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Http2ConnectionListenerOptions http2Options = new();
        configure(http2Options);

        return UseStreamListener(HttpProtocol.Http20, listener, (_, responseInterceptors) => new Http2ConnectionFactory(http2Options.Limits, responseInterceptors));
    }

    /// <summary>
    /// Serves HTTP/2 over the connection listener produced by the supplied factory, with default
    /// configuration.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp2(Func<IConnectionListener> listenerFactory)
    {
        return UseHttp2(listenerFactory, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/2 over the connection listener produced by the supplied factory, configured
    /// through <paramref name="configure"/> (for example, to tune the registration's
    /// <see cref="Http2ConnectionListenerOptions.Limits"/>).
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <param name="configure">Configures the HTTP/2-specific options (see <see cref="Http2ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listenerFactory"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp2(Func<IConnectionListener> listenerFactory, Action<Http2ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Http2ConnectionListenerOptions http2Options = new();
        configure(http2Options);

        return UseStreamListener(HttpProtocol.Http20, listenerFactory, (_, responseInterceptors) => new Http2ConnectionFactory(http2Options.Limits, responseInterceptors));
    }

    /// <summary>
    /// Serves HTTP/3 over the supplied multiplexed connection listener, with default
    /// (static-only QPACK) configuration.
    /// </summary>
    /// <param name="listener">The multiplexed listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp3(IMultiplexedConnectionListener listener)
    {
        return UseHttp3(listener, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/3 over the supplied multiplexed connection listener, configured through
    /// <paramref name="configure"/> (for example, to opt in to the QPACK dynamic table).
    /// </summary>
    /// <param name="listener">The multiplexed listener producing the transport connections.</param>
    /// <param name="configure">Configures the HTTP/3-specific options (see <see cref="Http3ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listener"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp3(IMultiplexedConnectionListener listener, Action<Http3ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(configure);

        return UseMultiplexedListener(() => listener, configure);
    }

    /// <summary>
    /// Serves HTTP/3 over the multiplexed connection listener produced by the supplied factory,
    /// with default (static-only QPACK) configuration.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked when the
    /// <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp3(Func<IMultiplexedConnectionListener> listenerFactory)
    {
        return UseHttp3(listenerFactory, static _ => { });
    }

    /// <summary>
    /// Serves HTTP/3 over the multiplexed connection listener produced by the supplied factory,
    /// configured through <paramref name="configure"/> (for example, to opt in to the QPACK dynamic table).
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked when the
    /// <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <param name="configure">Configures the HTTP/3-specific options (see <see cref="Http3ConnectionListenerOptions"/>).</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="listenerFactory"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp3(Func<IMultiplexedConnectionListener> listenerFactory, Action<Http3ConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);
        ArgumentNullException.ThrowIfNull(configure);

        return UseMultiplexedListener(listenerFactory, configure);
    }

    private HttpConnectionListenerOptions UseMultiplexedListener(Func<IMultiplexedConnectionListener> listenerFactory, Action<Http3ConnectionListenerOptions> configure)
    {
        Http3ConnectionListenerOptions http3Options = new();
        configure(http3Options);

        // The QPACK options are captured now (registration time); the listener-wide
        // response interceptors are bound when the HttpConnectionListener snapshots them.
        Registrations.Add(HttpListenerRegistration.ForMultiplexed(
            listenerFactory,
            responseInterceptors => new Http3ConnectionFactory(responseInterceptors, http3Options.QPack)));

        return this;
    }

    private HttpConnectionListenerOptions UseStreamListener(
        HttpProtocol protocol,
        IConnectionListener listener,
        Func<IHttpRequestInterceptor[], IHttpResponseInterceptor[], HttpConnectionFactory> connectionFactoryBuilder)
    {
        ArgumentNullException.ThrowIfNull(listener);

        HttpListenerRegistration.ValidateStreamCapabilities(listener.Capabilities, protocol, nameof(listener));

        Registrations.Add(HttpListenerRegistration.ForStream(protocol, () => listener, connectionFactoryBuilder));

        return this;
    }

    private HttpConnectionListenerOptions UseStreamListener(
        HttpProtocol protocol,
        Func<IConnectionListener> listenerFactory,
        Func<IHttpRequestInterceptor[], IHttpResponseInterceptor[], HttpConnectionFactory> connectionFactoryBuilder)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);

        Registrations.Add(HttpListenerRegistration.ForStream(protocol, listenerFactory, connectionFactoryBuilder));

        return this;
    }
}
