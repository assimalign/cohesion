using System;
using System.Collections.Generic;
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

        // Snapshot: registrations after this point must not race the accept loops or observe a
        // half-mutated list; the empty snapshots keep the parser's zero-interceptor fast paths.
        IHttpRequestInterceptor[] interceptors = [.. options.RequestInterceptors];
        IHttpResponseInterceptor[] responseInterceptors = [.. options.ResponseInterceptors];

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
                _multiplexedListeners.Add((registration.CreateMultiplexedConnectionFactory(responseInterceptors), registration.CreateMultiplexedListener()));
            }
            else
            {
                _streamListeners.Add((registration.CreateStreamConnectionFactory(interceptors, responseInterceptors), registration.CreateStreamListener()));
            }

            protocols |= registration.Protocol;
        }

        Protocols = protocols;
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
}
