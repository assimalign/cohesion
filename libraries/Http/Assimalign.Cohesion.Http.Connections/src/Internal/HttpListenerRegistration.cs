using System;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Binds a single HTTP protocol to the connection listener that will produce its transport
/// connections, and to the factory that turns each accepted transport connection into a
/// protocol-specific <see cref="HttpConnection"/>. Stream protocols (HTTP/1.1, HTTP/2) bind to an
/// <see cref="IConnectionListener"/> plus an <see cref="HttpConnectionFactory"/>; HTTP/3 binds to an
/// <see cref="IMultiplexedConnectionListener"/> plus an <see cref="HttpMultiplexedConnectionFactory"/>,
/// the multiplexed listener type itself acting as the shape gate.
/// </summary>
internal sealed class HttpListenerRegistration
{
    private readonly Func<IConnectionListener>? _streamListenerFactory;
    private readonly Func<IHttpRequestInterceptor[], IHttpResponseInterceptor[], HttpConnectionFactory>? _streamConnectionFactoryBuilder;
    private readonly Func<IMultiplexedConnectionListener>? _multiplexedListenerFactory;
    private readonly Func<IHttpResponseInterceptor[], HttpMultiplexedConnectionFactory>? _multiplexedConnectionFactoryBuilder;

    private HttpListenerRegistration(
        HttpProtocol protocol,
        Func<IConnectionListener>? streamListenerFactory,
        Func<IHttpRequestInterceptor[], IHttpResponseInterceptor[], HttpConnectionFactory>? streamConnectionFactoryBuilder,
        Func<IMultiplexedConnectionListener>? multiplexedListenerFactory,
        Func<IHttpResponseInterceptor[], HttpMultiplexedConnectionFactory>? multiplexedConnectionFactoryBuilder)
    {
        Protocol = protocol;
        _streamListenerFactory = streamListenerFactory;
        _streamConnectionFactoryBuilder = streamConnectionFactoryBuilder;
        _multiplexedListenerFactory = multiplexedListenerFactory;
        _multiplexedConnectionFactoryBuilder = multiplexedConnectionFactoryBuilder;
    }

    /// <summary>
    /// The single HTTP protocol this registration serves.
    /// </summary>
    public HttpProtocol Protocol { get; }

    /// <summary>
    /// Whether this registration binds a multiplexed (HTTP/3) listener.
    /// </summary>
    public bool IsMultiplexed => _multiplexedListenerFactory is not null;

    public static HttpListenerRegistration ForStream(
        HttpProtocol protocol,
        Func<IConnectionListener> listenerFactory,
        Func<IHttpRequestInterceptor[], IHttpResponseInterceptor[], HttpConnectionFactory> connectionFactoryBuilder)
    {
        return new HttpListenerRegistration(protocol, listenerFactory, connectionFactoryBuilder, multiplexedListenerFactory: null, multiplexedConnectionFactoryBuilder: null);
    }

    public static HttpListenerRegistration ForMultiplexed(
        Func<IMultiplexedConnectionListener> listenerFactory,
        Func<IHttpResponseInterceptor[], HttpMultiplexedConnectionFactory> connectionFactoryBuilder)
    {
        return new HttpListenerRegistration(HttpProtocol.Http30, streamListenerFactory: null, streamConnectionFactoryBuilder: null, listenerFactory, connectionFactoryBuilder);
    }

    /// <summary>
    /// Materializes the stream listener, re-validating its capabilities (factory-registered
    /// listeners cannot be inspected until they exist).
    /// </summary>
    public IConnectionListener CreateStreamListener()
    {
        IConnectionListener listener = _streamListenerFactory!.Invoke()
            ?? throw new InvalidOperationException($"The connection listener factory registered for '{Protocol}' returned null.");

        ValidateStreamCapabilities(listener.Capabilities, Protocol, paramName: null);

        return listener;
    }

    /// <summary>
    /// Builds the stream connection factory, binding it to the listener-wide request/response
    /// interceptors (which are only snapshotted once the listener is constructed); the
    /// registration's captured per-version options (limits) are already closed over by the
    /// builder.
    /// </summary>
    /// <param name="interceptors">The snapshotted request-parse interceptors.</param>
    /// <param name="responseInterceptors">The snapshotted response interceptors.</param>
    /// <returns>The stream connection factory.</returns>
    public HttpConnectionFactory CreateStreamConnectionFactory(IHttpRequestInterceptor[] interceptors, IHttpResponseInterceptor[] responseInterceptors)
    {
        return _streamConnectionFactoryBuilder!.Invoke(interceptors, responseInterceptors);
    }

    /// <summary>
    /// Materializes the multiplexed listener.
    /// </summary>
    public IMultiplexedConnectionListener CreateMultiplexedListener()
    {
        return _multiplexedListenerFactory!.Invoke()
            ?? throw new InvalidOperationException($"The multiplexed connection listener factory registered for '{Protocol}' returned null.");
    }

    /// <summary>
    /// Builds the multiplexed (HTTP/3) connection factory, binding it to the listener-wide
    /// response interceptors (which are only snapshotted once the listener is constructed);
    /// the registration's captured HTTP/3 options are already closed over by the builder.
    /// </summary>
    /// <param name="responseInterceptors">The snapshotted response interceptors.</param>
    /// <returns>The multiplexed connection factory.</returns>
    public HttpMultiplexedConnectionFactory CreateMultiplexedConnectionFactory(IHttpResponseInterceptor[] responseInterceptors)
    {
        return _multiplexedConnectionFactoryBuilder!.Invoke(responseInterceptors);
    }

    /// <summary>
    /// Gates a stream-protocol registration on transport capabilities (never on protocol
    /// identity): HTTP/1.1 and HTTP/2 require a reliable, ordered byte stream.
    /// </summary>
    /// <param name="capabilities">The capabilities reported by the candidate listener.</param>
    /// <param name="protocol">The HTTP protocol being registered.</param>
    /// <param name="paramName">The argument name to blame, when validating an argument.</param>
    /// <exception cref="ArgumentException">Thrown when the capabilities do not satisfy the protocol's requirements.</exception>
    public static void ValidateStreamCapabilities(ConnectionCapabilities capabilities, HttpProtocol protocol, string? paramName)
    {
        if (capabilities.Delivery == ConnectionDelivery.Stream && capabilities.IsReliable && capabilities.IsOrdered)
        {
            return;
        }

        string protocolName = protocol == HttpProtocol.Http20 ? "HTTP/2" : "HTTP/1.1";
        string message =
            $"{protocolName} requires a transport whose capabilities report a reliable, ordered byte stream " +
            $"(Delivery=Stream, IsReliable=true, IsOrdered=true); the supplied listener reports " +
            $"Delivery={capabilities.Delivery}, IsReliable={capabilities.IsReliable}, IsOrdered={capabilities.IsOrdered}.";

        throw paramName is null
            ? new ArgumentException(message)
            : new ArgumentException(message, paramName);
    }
}
