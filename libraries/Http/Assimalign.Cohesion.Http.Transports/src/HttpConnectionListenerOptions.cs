using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

using Assimalign.Cohesion.Http.Transports.Internal;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Configures the underlying transports used by an <see cref="HttpConnectionListener"/>.
/// </summary>
public sealed class HttpConnectionListenerOptions
{
    private readonly List<HttpProtocolRegistration> _registrations = new();
    private int _backlogCapacity = 512;

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
    /// Gets the configured HTTP protocols.
    /// </summary>
    public HttpProtocol Protocols => _registrations.Aggregate(HttpProtocol.None, static (current, registration) => current | registration.Protocol);

    /// <summary>
    /// Gets the configured transports.
    /// </summary>
    public IReadOnlyCollection<ITransport> Transports => _registrations.Select(static registration => registration.Transport).ToArray();

    /// <summary>
    /// Adds a pre-configured transport for the supplied HTTP protocol.
    /// </summary>
    /// <param name="protocol">The HTTP protocol implemented by the transport.</param>
    /// <param name="transport">The transport to use.</param>
    /// <param name="isSecure">Indicates whether connections accepted from the transport are secured.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseTransport(HttpProtocol protocol, ITransport transport, bool isSecure = false)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _registrations.Add(new HttpProtocolRegistration(protocol, transport, isSecure));

        return this;
    }

    /// <summary>
    /// Adds a TCP transport configured for HTTP/1.1.
    /// </summary>
    /// <param name="configure">The transport configuration callback.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp1(Action<TcpServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseTransport(HttpProtocol.Http11, TcpServerTransport.Create(configure));
    }

    /// <summary>
    /// Adds a TCP transport configured for prior-knowledge HTTP/2.
    /// </summary>
    /// <param name="configure">The transport configuration callback.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseHttp2(Action<TcpServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseTransport(HttpProtocol.Http20, TcpServerTransport.Create(configure));
    }

    /// <summary>
    /// Adds a QUIC transport configured for HTTP/3.
    /// </summary>
    /// <param name="configure">The transport configuration callback.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("osx")]
    public HttpConnectionListenerOptions UseHttp3(Action<QuicServerTransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        return UseTransport(HttpProtocol.Http30, QuicServerTransport.Create(configure), isSecure: true);
    }

    internal IReadOnlyList<HttpProtocolRegistration> GetRegistrations()
    {
        return _registrations;
    }
}
