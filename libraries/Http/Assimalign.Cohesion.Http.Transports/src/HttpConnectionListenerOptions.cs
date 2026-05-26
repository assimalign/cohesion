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
    public IReadOnlyCollection<ServerTransport> Transports => _registrations.Select(static registration => registration.Transport).ToArray();

    /// <summary>
    /// Gets or sets a factory invoked once per <see cref="IHttpContext"/>
    /// at construction time to produce the request-scoped
    /// <see cref="IHttpFeatureCollection"/> that backs
    /// <see cref="IHttpContext.Features"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Feature lifetime is intentionally bound to the
    /// <see cref="IHttpContext"/> rather than the underlying connection:
    /// <see cref="IHttpContext"/> is <see cref="IAsyncDisposable"/>, so
    /// features that own disposable state (handles, scoped service
    /// providers, cryptographic material) get deterministic cleanup at
    /// the end of every request. Any feature in the returned collection
    /// that implements <see cref="IAsyncDisposable"/> or
    /// <see cref="IDisposable"/> is disposed when the owning context
    /// disposes.
    /// </para>
    /// <para>
    /// The factory is invoked synchronously on the receive path each time
    /// a new <see cref="IHttpContext"/> is materialised &#8212; once per
    /// HTTP/1.1 request, once per HTTP/2 / HTTP/3 stream. Keep it
    /// inexpensive (no I/O); the returned collection becomes the request's
    /// <see cref="IHttpContext.Features"/> directly, and any feature it
    /// already carries is visible to the first middleware to read
    /// <see cref="IHttpContext.Features"/>.
    /// </para>
    /// <para>
    /// Leave this property <see langword="null"/> to opt out &#8212; each
    /// request then gets a fresh empty feature collection, which is the
    /// default behaviour.
    /// </para>
    /// </remarks>
    public Func<IHttpFeatureCollection>? CreateFeatures { get; set; }

    /// <summary>
    /// Adds a pre-configured transport for the supplied HTTP protocol.
    /// </summary>
    /// <param name="protocol">The HTTP protocol implemented by the transport.</param>
    /// <param name="transport">The transport to use.</param>
    /// <param name="isSecure">Indicates whether connections accepted from the transport are secured.</param>
    /// <returns>The current options instance.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> is <see langword="null"/>.</exception>
    public HttpConnectionListenerOptions UseTransport(HttpProtocol protocol, ServerTransport transport, bool isSecure = false)
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