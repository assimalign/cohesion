using System;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Transports.Internal;

/// <summary>
/// Binds a single HTTP protocol to the connection listener that will produce its transport
/// connections. Stream protocols (HTTP/1.1, HTTP/2) bind to an <see cref="IConnectionListener"/>;
/// HTTP/3 binds to an <see cref="IMultiplexedConnectionListener"/>, the type itself acting as the
/// shape gate.
/// </summary>
internal sealed class HttpListenerRegistration
{
    private readonly Func<IConnectionListener>? _streamListenerFactory;
    private readonly Func<IMultiplexedConnectionListener>? _multiplexedListenerFactory;

    private HttpListenerRegistration(
        HttpProtocol protocol,
        Func<IConnectionListener>? streamListenerFactory,
        Func<IMultiplexedConnectionListener>? multiplexedListenerFactory)
    {
        Protocol = protocol;
        _streamListenerFactory = streamListenerFactory;
        _multiplexedListenerFactory = multiplexedListenerFactory;
    }

    /// <summary>
    /// The single HTTP protocol this registration serves.
    /// </summary>
    public HttpProtocol Protocol { get; }

    /// <summary>
    /// Whether this registration binds a multiplexed (HTTP/3) listener.
    /// </summary>
    public bool IsMultiplexed => _multiplexedListenerFactory is not null;

    public static HttpListenerRegistration ForStream(HttpProtocol protocol, Func<IConnectionListener> listenerFactory)
    {
        return new HttpListenerRegistration(protocol, listenerFactory, multiplexedListenerFactory: null);
    }

    public static HttpListenerRegistration ForMultiplexed(Func<IMultiplexedConnectionListener> listenerFactory)
    {
        return new HttpListenerRegistration(HttpProtocol.Http30, streamListenerFactory: null, listenerFactory);
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
    /// Materializes the multiplexed listener.
    /// </summary>
    public IMultiplexedConnectionListener CreateMultiplexedListener()
    {
        return _multiplexedListenerFactory!.Invoke()
            ?? throw new InvalidOperationException($"The multiplexed connection listener factory registered for '{Protocol}' returned null.");
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
