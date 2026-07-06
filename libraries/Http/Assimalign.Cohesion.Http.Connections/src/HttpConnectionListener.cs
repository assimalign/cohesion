using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Http.Connections.Internal;
using Assimalign.Cohesion.Http.Connections.Internal.Http1;
using Assimalign.Cohesion.Http.Connections.Internal.Http2;
using Assimalign.Cohesion.Http.Connections.Internal.Http3;

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
    private readonly List<(HttpProtocol Protocol, IConnectionListener Listener)> _streamListeners;
    private readonly List<IMultiplexedConnectionListener> _multiplexedListeners;
    private readonly HttpServerLimits _limits;
    private readonly IHttpRequestInterceptor[] _interceptors;
    private readonly IHttpResponseInterceptor[] _responseInterceptors;
    private readonly Http3QPackOptions _qpackOptions;
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

        _streamListeners = new List<(HttpProtocol, IConnectionListener)>();
        _multiplexedListeners = new List<IMultiplexedConnectionListener>();
        _limits = options.Limits;
        _qpackOptions = options.QPack;
        // Snapshot: registrations after this point must not race the accept loops or observe a
        // half-mutated list; the empty snapshot keeps the parser's zero-interceptor fast path.
        _interceptors = [.. options.Interceptors];
        _responseInterceptors = [.. options.ResponseInterceptors];

        HttpProtocol protocols = HttpProtocol.None;

        foreach (HttpListenerRegistration registration in options.Registrations)
        {
            if (registration.IsMultiplexed)
            {
                _multiplexedListeners.Add(registration.CreateMultiplexedListener());
            }
            else
            {
                _streamListeners.Add((registration.Protocol, registration.CreateStreamListener()));
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

        foreach ((HttpProtocol _, IConnectionListener listener) in _streamListeners)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        foreach (IMultiplexedConnectionListener listener in _multiplexedListeners)
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

            foreach ((HttpProtocol protocol, IConnectionListener listener) in _streamListeners)
            {
                _acceptLoops.Add(RunStreamAcceptLoopAsync(protocol, listener));
            }

            foreach (IMultiplexedConnectionListener listener in _multiplexedListeners)
            {
                _acceptLoops.Add(RunMultiplexedAcceptLoopAsync(listener));
            }

            _acceptLoopsStarted = true;
        }
    }

    private async Task RunStreamAcceptLoopAsync(HttpProtocol protocol, IConnectionListener listener)
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

                HttpConnection httpConnection = protocol switch
                {
                    HttpProtocol.Http11 => new Http1Connection(connection, isSecure, _limits, _interceptors, _responseInterceptors),
                    HttpProtocol.Http20 => new Http2Connection(connection, isSecure, _responseInterceptors),
                    _ => throw new InvalidOperationException($"The configured HTTP protocol '{protocol}' does not map to a stream connection listener.")
                };

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

    private async Task RunMultiplexedAcceptLoopAsync(IMultiplexedConnectionListener listener)
    {
        bool isSecure = listener.Capabilities.Security == ConnectionSecurity.Tls;

        try
        {
            while (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                IMultiplexedConnection multiplexedConnection = await listener
                    .AcceptAsync(_disposeCancellationTokenSource.Token)
                    .ConfigureAwait(false);

                HttpConnection httpConnection = CreateHttp3Connection(multiplexedConnection, isSecure, _responseInterceptors, _qpackOptions);

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

    private static Http3Connection CreateHttp3Connection(IMultiplexedConnection connection, bool isSecure, IHttpResponseInterceptor[] responseInterceptors, Http3QPackOptions qpackOptions)
    {
        if (!IsHttp3SupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return new Http3Connection(connection, isSecure, responseInterceptors, qpackOptions);
    }

    [SupportedOSPlatformGuard("windows")]
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    private static bool IsHttp3SupportedPlatform()
    {
        return OperatingSystem.IsWindows() ||
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS();
    }
}
