using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Transports.Internal;

namespace Assimalign.Cohesion.Http.Transports;

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
    /// Serves HTTP/1.1 over the supplied connection listener.
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp1(IConnectionListener listener)
    {
        return UseStreamListener(HttpProtocol.Http11, listener);
    }

    /// <summary>
    /// Serves HTTP/1.1 over the connection listener produced by the supplied factory.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp1(Func<IConnectionListener> listenerFactory)
    {
        return UseStreamListener(HttpProtocol.Http11, listenerFactory);
    }

    /// <summary>
    /// Serves HTTP/2 over the supplied connection listener.
    /// </summary>
    /// <param name="listener">The listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the listener's capabilities do not report a reliable, ordered byte stream.
    /// </exception>
    public HttpConnectionListenerOptions UseHttp2(IConnectionListener listener)
    {
        return UseStreamListener(HttpProtocol.Http20, listener);
    }

    /// <summary>
    /// Serves HTTP/2 over the connection listener produced by the supplied factory.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked (and its result capability-validated)
    /// when the <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp2(Func<IConnectionListener> listenerFactory)
    {
        return UseStreamListener(HttpProtocol.Http20, listenerFactory);
    }

    /// <summary>
    /// Serves HTTP/3 over the supplied multiplexed connection listener.
    /// </summary>
    /// <param name="listener">The multiplexed listener producing the transport connections.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp3(IMultiplexedConnectionListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        Registrations.Add(HttpListenerRegistration.ForMultiplexed(() => listener));

        return this;
    }

    /// <summary>
    /// Serves HTTP/3 over the multiplexed connection listener produced by the supplied factory.
    /// </summary>
    /// <param name="listenerFactory">
    /// The factory producing the listener; it is invoked when the
    /// <see cref="HttpConnectionListener"/> is constructed.
    /// </param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listenerFactory"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp3(Func<IMultiplexedConnectionListener> listenerFactory)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);

        Registrations.Add(HttpListenerRegistration.ForMultiplexed(listenerFactory));

        return this;
    }

    private HttpConnectionListenerOptions UseStreamListener(HttpProtocol protocol, IConnectionListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        HttpListenerRegistration.ValidateStreamCapabilities(listener.Capabilities, protocol, nameof(listener));

        Registrations.Add(HttpListenerRegistration.ForStream(protocol, () => listener));

        return this;
    }

    private HttpConnectionListenerOptions UseStreamListener(HttpProtocol protocol, Func<IConnectionListener> listenerFactory)
    {
        ArgumentNullException.ThrowIfNull(listenerFactory);

        Registrations.Add(HttpListenerRegistration.ForStream(protocol, listenerFactory));

        return this;
    }
}
