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
    private readonly SemaphoreSlim _bindSemaphore;
    private readonly bool _advertiseAltService;
    private readonly TimeSpan _altServiceMaxAge;
    private readonly string? _altServiceAuthority;
    private bool _acceptLoopsStarted;
    private bool _isBound;
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

        // Snapshot advertisement configuration with the rest of the listener-wide options. The
        // header itself is computed after BindAsync because a port-zero QUIC listener does not know
        // its advertised port until the transport bind completes.
        _advertiseAltService = options.AltServiceAdvertisement.Enabled;
        _altServiceMaxAge = options.AltServiceAdvertisement.MaxAge;
        _altServiceAuthority = options.AltServiceAdvertisement.Authority;

        _acceptLoops = new List<Task>(_streamListeners.Count + _multiplexedListeners.Count);
        _acceptedConnections = Channel.CreateBounded<HttpConnection>(new BoundedChannelOptions(options.BacklogCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _disposeCancellationTokenSource = new CancellationTokenSource();
        _acceptLoopLock = new Lock();
        _bindSemaphore = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets the configured HTTP protocols supported by this listener.
    /// </summary>
    public HttpProtocol Protocols { get; }

    /// <inheritdoc />
    public async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        await _bindSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, nameof(HttpConnectionListener));

            if (_isBound)
            {
                return;
            }

            if (_streamListeners.Count == 0 && _multiplexedListeners.Count == 0)
            {
                throw new InvalidOperationException("At least one connection listener must be configured before binding HTTP connections.");
            }

            try
            {
                foreach ((HttpConnectionFactory _, IConnectionListener listener) in _streamListeners)
                {
                    await listener.BindAsync(cancellationToken).ConfigureAwait(false);
                }

                foreach ((HttpMultiplexedConnectionFactory _, IMultiplexedConnectionListener listener) in _multiplexedListeners)
                {
                    await listener.BindAsync(cancellationToken).ConfigureAwait(false);
                }

                ConfigureAltServiceAdvertisement();
                _isBound = true;
            }
            catch
            {
                // Binding is transactional at the aggregate boundary. Preserve the original bind
                // failure while releasing every listener, including one that failed after acquiring
                // an operating-system resource of its own.
                try
                {
                    await DisposeCoreAsync().ConfigureAwait(false);
                }
                catch
                {
                    // The bind exception is the actionable startup failure. Cleanup failures must
                    // not replace it; all listeners were still given a release attempt.
                }

                throw;
            }
        }
        finally
        {
            _bindSemaphore.Release();
        }
    }

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
        bool enabled,
        TimeSpan maxAge,
        string? authority,
        List<(HttpMultiplexedConnectionFactory Factory, IMultiplexedConnectionListener Listener)> multiplexedListeners,
        int streamListenerCount)
    {
        if (!enabled || multiplexedListeners.Count == 0 || streamListenerCount == 0)
        {
            return null;
        }

        long maxAgeSeconds = (long)maxAge.TotalSeconds;

        HttpAltService alternative;
        if (!string.IsNullOrEmpty(authority))
        {
            if (!TryParseAuthority(authority, out string? host, out int port))
            {
                throw new InvalidOperationException(
                    $"The configured Alt-Svc authority '{authority}' is not a valid 'host:port' or ':port' value.");
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
        await BindAsync(cancellationToken).ConfigureAwait(false);

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
        await _bindSemaphore.WaitAsync().ConfigureAwait(false);

        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _bindSemaphore.Release();
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        List<Exception>? disposalFailures = null;

        try
        {
            _disposeCancellationTokenSource.Cancel();
        }
        catch (Exception exception)
        {
            (disposalFailures ??= new List<Exception>()).Add(exception);
        }

        foreach ((HttpConnectionFactory _, IConnectionListener listener) in _streamListeners)
        {
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (disposalFailures ??= new List<Exception>()).Add(exception);
            }
        }

        foreach ((HttpMultiplexedConnectionFactory _, IMultiplexedConnectionListener listener) in _multiplexedListeners)
        {
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (disposalFailures ??= new List<Exception>()).Add(exception);
            }
        }

        Task[] acceptLoops;

        lock (_acceptLoopLock)
        {
            acceptLoops = _acceptLoops.ToArray();
        }

        if (acceptLoops.Length > 0)
        {
            try
            {
                await Task.WhenAll(acceptLoops).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (disposalFailures ??= new List<Exception>()).Add(exception);
            }
        }

        while (_acceptedConnections.Reader.TryRead(out HttpConnection? connection))
        {
            if (connection is null)
            {
                continue;
            }

            try
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (disposalFailures ??= new List<Exception>()).Add(exception);
            }
        }

        _acceptedConnections.Writer.TryComplete();
        _disposeCancellationTokenSource.Dispose();

        if (disposalFailures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(disposalFailures[0]).Throw();
        }

        if (disposalFailures is { Count: > 1 })
        {
            throw new AggregateException("One or more HTTP connection listeners failed to dispose.", disposalFailures);
        }
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

    private void ConfigureAltServiceAdvertisement()
    {
        string? altSvcHeaderValue = BuildAltSvcHeaderValue(
            _advertiseAltService,
            _altServiceMaxAge,
            _altServiceAuthority,
            _multiplexedListeners,
            _streamListeners.Count);

        if (altSvcHeaderValue is null)
        {
            return;
        }

        foreach ((HttpConnectionFactory factory, IConnectionListener _) in _streamListeners)
        {
            factory.AltSvcHeaderValue = altSvcHeaderValue;
        }
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

                HttpConnection httpConnection;

                try
                {
                    httpConnection = connectionFactory.Create(connection, isSecure);
                }
                catch
                {
                    await DisposeAfterFailedTransferAsync(connection).ConfigureAwait(false);
                    throw;
                }

                await QueueAcceptedConnectionAsync(httpConnection).ConfigureAwait(false);
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

                HttpConnection httpConnection;

                try
                {
                    httpConnection = connectionFactory.Create(multiplexedConnection, isSecure);
                }
                catch
                {
                    await DisposeAfterFailedTransferAsync(multiplexedConnection).ConfigureAwait(false);
                    throw;
                }

                await QueueAcceptedConnectionAsync(httpConnection).ConfigureAwait(false);
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

    private async Task QueueAcceptedConnectionAsync(HttpConnection connection)
    {
        try
        {
            await _acceptedConnections.Writer
                .WriteAsync(connection, _disposeCancellationTokenSource.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Once a transport accept returns, the aggregate owns its HTTP wrapper. If shutdown
            // cancels a bounded-channel write, the wrapper never reaches the backlog drain and must
            // be released here. Preserve the write/cancellation failure if cleanup also fails.
            await DisposeAfterFailedTransferAsync(connection).ConfigureAwait(false);

            throw;
        }
    }

    private static async ValueTask DisposeAfterFailedTransferAsync(IAsyncDisposable connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The failure that prevented ownership transfer remains the actionable exception.
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
