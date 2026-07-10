using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Accepts transport connections from the configured connection listeners and adapts them into
/// HTTP protocol connections.
/// </summary>
/// <remarks>
/// One accept loop runs per registered listener: HTTP/1.1 and HTTP/2 loops accept
/// <see cref="IConnection"/> instances from an <see cref="IConnectionListener"/>, the HTTP/3
/// loop accepts <see cref="IMultiplexedConnection"/> instances from an
/// <see cref="IMultiplexedConnectionListener"/>. Accepted connections are buffered on a bounded
/// channel drained by <see cref="AcceptOrListenAsync(CancellationToken)"/>.
/// </remarks>
public sealed class HttpConnectionListener : IHttpConnectionListener
{
    private readonly List<(HttpConnectionFactory Factory, IConnectionListener Listener)> _streamListeners;
    private readonly List<(HttpMultiplexedConnectionFactory Factory, IMultiplexedConnectionListener Listener)> _multiplexedListeners;
    private readonly List<Task> _acceptLoops;
    private readonly Channel<HttpConnection> _acceptedConnections;
    private readonly CancellationTokenSource _disposeCancellationTokenSource;
    private readonly Lock _acceptLoopLock;
    private bool _acceptLoopsStarted;
    private bool _isDisposed;
    private volatile Exception? _acceptLoopException;

    /// <summary>
    /// Initializes a new HTTP connection listener, materializing every registered listener
    /// (factory registrations are invoked and capability-validated here).
    /// </summary>
    /// <param name="options">The configured listener options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when a factory-registered HTTP/1.1 or HTTP/2 listener does not report a reliable,
    /// ordered byte stream.
    /// </exception>
    public HttpConnectionListener(HttpConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _streamListeners = new List<(HttpConnectionFactory, IConnectionListener)>();
        _multiplexedListeners = new List<(HttpMultiplexedConnectionFactory, IMultiplexedConnectionListener)>();

        // Snapshot the single interceptor list once (registrations after this point must not race
        // the accept loops or observe a half-mutated list), then partition it by declared scope —
        // registration order preserved within each partition. The partitioning is what keeps the
        // zero-cost fast paths scope-exact: an all-request registration produces an empty response
        // partition (no sink or exchange control is ever constructed), and vice versa.
        IHttpExchangeInterceptor[] snapshot = [.. options.Interceptors];
        IHttpExchangeInterceptor[] interceptors = FilterByScope(snapshot, HttpInterceptorScopes.Request);
        IHttpExchangeInterceptor[] responseInterceptors = FilterByScope(snapshot, HttpInterceptorScopes.Response);

        HttpProtocol protocols = HttpProtocol.None;

        // Each registration carries the factory that turns an accepted transport connection into
        // its protocol-specific HttpConnection; the accept loops dispatch to that factory rather
        // than switching on the protocol. Factories bind to the listener-wide interceptors here
        // (once they are snapshotted); each registration's version-specific options (limits,
        // QPACK) were already captured at Use* time and are closed over by its factory builder.
        foreach (HttpListenerRegistration registration in options.Registrations)
        {
            if (registration.IsMultiplexed)
            {
                _multiplexedListeners.Add((registration.CreateMultiplexedConnectionFactory(interceptors, responseInterceptors), registration.CreateMultiplexedListener()));
            }
            else
            {
                _streamListeners.Add((registration.CreateStreamConnectionFactory(interceptors, responseInterceptors), registration.CreateStreamListener()));
            }

            protocols |= registration.Protocol;
        }

        Protocols = protocols;

        // Compute the Alt-Svc advertisement once — every listener has now been materialized, so the
        // HTTP/3 endpoint (if any) is known — and push it onto the stream factories that inject it on
        // their responses. Set before the first connection is accepted (accept loops start lazily on
        // the first AcceptOrListenAsync), so no factory observes a half-set value.
        string? altSvcHeaderValue = BuildAltSvcHeaderValue(options.AltServiceAdvertisement, _multiplexedListeners, _streamListeners.Count);
        if (altSvcHeaderValue is not null)
        {
            foreach ((HttpConnectionFactory factory, IConnectionListener _) in _streamListeners)
            {
                factory.AltSvcHeaderValue = altSvcHeaderValue;
            }
        }

        _acceptLoops = new List<Task>(_streamListeners.Count + _multiplexedListeners.Count);
        _acceptedConnections = Channel.CreateBounded<HttpConnection>(new BoundedChannelOptions(options.BacklogCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _disposeCancellationTokenSource = new CancellationTokenSource();
        _acceptLoopLock = new Lock();
    }

    /// <summary>
    /// Gets the configured HTTP protocols supported by this listener.
    /// </summary>
    public HttpProtocol Protocols { get; }

    /// <summary>
    /// Builds the RFC 7838 <c>Alt-Svc</c> header value the stream protocols advertise, or
    /// <see langword="null"/> when advertisement does not apply. Advertisement requires the opt-in
    /// flag, at least one HTTP/3 listener to advertise, and at least one stream listener to carry the
    /// header. The h3 port is taken from the first HTTP/3 listener endpoint unless an explicit
    /// authority is configured.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when advertisement is enabled but the h3 port cannot be determined — the configured
    /// authority is malformed, or the HTTP/3 listener endpoint exposes no port and no explicit
    /// authority was supplied.
    /// </exception>
    private static string? BuildAltSvcHeaderValue(
        HttpAltServiceAdvertisementOptions advertisement,
        List<(HttpMultiplexedConnectionFactory Factory, IMultiplexedConnectionListener Listener)> multiplexedListeners,
        int streamListenerCount)
    {
        if (!advertisement.Enabled || multiplexedListeners.Count == 0 || streamListenerCount == 0)
        {
            return null;
        }

        long maxAgeSeconds = (long)advertisement.MaxAge.TotalSeconds;

        HttpAltService alternative;
        if (!string.IsNullOrEmpty(advertisement.Authority))
        {
            if (!TryParseAuthority(advertisement.Authority, out string? host, out int port))
            {
                throw new InvalidOperationException(
                    $"The configured Alt-Svc authority '{advertisement.Authority}' is not a valid 'host:port' or ':port' value.");
            }

            alternative = HttpAltService.Http3(host, port, maxAgeSeconds);
        }
        else if (TryGetPort(multiplexedListeners[0].Listener.EndPoint, out int derivedPort))
        {
            // Advertise on the request's own host (empty host); only the port is carried over.
            alternative = HttpAltService.Http3(host: null, derivedPort, maxAgeSeconds);
        }
        else
        {
            throw new InvalidOperationException(
                "Alt-Svc advertisement is enabled but the HTTP/3 listener endpoint does not expose a port; " +
                $"set {nameof(HttpAltServiceAdvertisementOptions)}.{nameof(HttpAltServiceAdvertisementOptions.Authority)} explicitly.");
        }

        return alternative.Format();
    }

    private static bool TryGetPort(EndPoint endPoint, out int port)
    {
        switch (endPoint)
        {
            case IPEndPoint ipEndPoint:
                port = ipEndPoint.Port;
                return true;
            case DnsEndPoint dnsEndPoint:
                port = dnsEndPoint.Port;
                return true;
            default:
                port = 0;
                return false;
        }
    }

    private static bool TryParseAuthority(string authority, out string? host, out int port)
    {
        host = null;
        port = 0;

        int colonIndex = authority.LastIndexOf(':');
        if (colonIndex < 0
            || !int.TryParse(authority.AsSpan(colonIndex + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedPort)
            || parsedPort < 0
            || parsedPort > 65535)
        {
            return false;
        }

        port = parsedPort;
        if (colonIndex > 0)
        {
            host = authority.Substring(0, colonIndex);
        }

        return true;
    }

    /// <summary>
    /// Accepts the next available HTTP connection from the configured connection listeners.
    /// </summary>
    /// <remarks>
    /// If an accept loop faults, the transport listener's original exception is rethrown from
    /// pending and subsequent accept calls rather than being reported as disposal.
    /// </remarks>
    /// <param name="cancellationToken">The cancellation token for the accept operation.</param>
    /// <returns>The next accepted HTTP connection.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no connection listener has been configured.</exception>
    public async Task<HttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(HttpConnectionListener));

        if (_streamListeners.Count == 0 && _multiplexedListeners.Count == 0)
        {
            throw new InvalidOperationException("At least one connection listener must be configured before accepting HTTP connections.");
        }

        EnsureAcceptLoopsStarted();

        using CancellationTokenSource linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellationTokenSource.Token);

        try
        {
            return await _acceptedConnections.Reader.ReadAsync(linkedCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCancellationTokenSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // An accept-loop failure cancels the dispose token, which preempts the read before
            // it can observe the channel's faulted completion; surface the loop's exception
            // instead of reporting disposal.
            if (_acceptLoopException is { } acceptLoopException)
            {
                ExceptionDispatchInfo.Capture(acceptLoopException).Throw();
            }

            throw new ObjectDisposedException(nameof(HttpConnectionListener));
        }
        catch (ChannelClosedException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(HttpConnectionListener));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeCancellationTokenSource.Cancel();

        foreach ((HttpConnectionFactory _, IConnectionListener listener) in _streamListeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        foreach ((HttpMultiplexedConnectionFactory _, IMultiplexedConnectionListener listener) in _multiplexedListeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        Task[] acceptLoops;

        lock (_acceptLoopLock)
        {
            acceptLoops = _acceptLoops.ToArray();
        }

        if (acceptLoops.Length > 0)
        {
            await Task.WhenAll(acceptLoops).ConfigureAwait(false);
        }

        _acceptedConnections.Writer.TryComplete();
        _disposeCancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Creates a configured HTTP connection listener.
    /// </summary>
    /// <param name="configure">The listener configuration callback.</param>
    /// <returns>A configured <see cref="HttpConnectionListener"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static HttpConnectionListener Create(Action<HttpConnectionListenerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        HttpConnectionListenerOptions options = new();

        configure(options);

        return new HttpConnectionListener(options);
    }

    async Task<IHttpConnection> IHttpConnectionListener.AcceptOrListenAsync(CancellationToken cancellationToken)
    {
        return await AcceptOrListenAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureAcceptLoopsStarted()
    {
        lock (_acceptLoopLock)
        {
            if (_acceptLoopsStarted)
            {
                return;
            }

            foreach ((HttpConnectionFactory factory, IConnectionListener listener) in _streamListeners)
            {
                _acceptLoops.Add(RunStreamAcceptLoopAsync(factory, listener));
            }

            foreach ((HttpMultiplexedConnectionFactory factory, IMultiplexedConnectionListener listener) in _multiplexedListeners)
            {
                _acceptLoops.Add(RunMultiplexedAcceptLoopAsync(factory, listener));
            }

            _acceptLoopsStarted = true;
        }
    }

    private async Task RunStreamAcceptLoopAsync(HttpConnectionFactory connectionFactory, IConnectionListener listener)
    {
        // TLS is composed onto the listener before registration; the capability
        // reports the effective security of every connection it accepts.
        bool isSecure = listener.Capabilities.Security == ConnectionSecurity.Tls;

        try
        {
            while (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                IConnection connection = await listener
                    .AcceptAsync(_disposeCancellationTokenSource.Token)
                    .ConfigureAwait(false);

                HttpConnection httpConnection = connectionFactory.Create(connection, isSecure);

                await _acceptedConnections.Writer.WriteAsync(httpConnection, _disposeCancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (ObjectDisposedException) when (_isDisposed || _disposeCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Complete the channel before cancelling the dispose token so a pending accept
            // observes the listener's failure rather than the cancellation; the recorded
            // exception covers accepts that begin after the token is already cancelled.
            if (_acceptedConnections.Writer.TryComplete(exception))
            {
                _acceptLoopException = exception;
            }

            _disposeCancellationTokenSource.Cancel();
        }
    }

    private async Task RunMultiplexedAcceptLoopAsync(HttpMultiplexedConnectionFactory connectionFactory, IMultiplexedConnectionListener listener)
    {
        bool isSecure = listener.Capabilities.Security == ConnectionSecurity.Tls;

        try
        {
            while (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                IMultiplexedConnection multiplexedConnection = await listener
                    .AcceptAsync(_disposeCancellationTokenSource.Token)
                    .ConfigureAwait(false);

                HttpConnection httpConnection = connectionFactory.Create(multiplexedConnection, isSecure);

                await _acceptedConnections.Writer.WriteAsync(httpConnection, _disposeCancellationTokenSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (ObjectDisposedException) when (_isDisposed || _disposeCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Complete the channel before cancelling the dispose token so a pending accept
            // observes the listener's failure rather than the cancellation; the recorded
            // exception covers accepts that begin after the token is already cancelled.
            if (_acceptedConnections.Writer.TryComplete(exception))
            {
                _acceptLoopException = exception;
            }

            _disposeCancellationTokenSource.Cancel();
        }
    }

    /// <summary>
    /// Partitions the snapshotted interceptors by declared scope, preserving registration order.
    /// Each interceptor's <see cref="IHttpExchangeInterceptor.Scopes"/> is read exactly once (the
    /// contract makes it constant, and a single read keeps a misbehaving implementation from
    /// desynchronizing the partition). Runs once, at listener construction; the resulting arrays
    /// live for the listener's lifetime and are shared by every connection it accepts.
    /// </summary>
    private static IHttpExchangeInterceptor[] FilterByScope(IHttpExchangeInterceptor[] snapshot, HttpInterceptorScopes scope)
    {
        List<IHttpExchangeInterceptor> filtered = new(snapshot.Length);

        foreach (IHttpExchangeInterceptor interceptor in snapshot)
        {
            if ((interceptor.Scopes & scope) != 0)
            {
                filtered.Add(interceptor);
            }
        }

        if (filtered.Count == snapshot.Length)
        {
            return snapshot;
        }

        return filtered.Count == 0 ? Array.Empty<IHttpExchangeInterceptor>() : [.. filtered];
    }
}
