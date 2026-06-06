using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Transports;

using Assimalign.Cohesion.Http.Transports.Internal;
using Assimalign.Cohesion.Http.Transports.Internal.Http1;
using Assimalign.Cohesion.Http.Transports.Internal.Http2;
using Assimalign.Cohesion.Http.Transports.Internal.Http3;
using Assimalign.Cohesion.Transports;

/// <summary>
/// Accepts transport connections and adapts them into HTTP protocol connections.
/// </summary>
public sealed class HttpConnectionListener : ServerTransport<HttpConnection>, IHttpConnectionListener
{
    private readonly List<HttpConnectionTransport> _transports;
    private readonly List<Task> _acceptLoops;
    private readonly Channel<HttpConnection> _acceptedConnections;
    private readonly CancellationTokenSource _disposeCancellationTokenSource;
    private readonly Lock _acceptLoopLock;
    private bool _acceptLoopsStarted;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new HTTP connection listener.
    /// </summary>
    /// <param name="options">The configured listener options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public HttpConnectionListener(HttpConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _transports = options.Transports.Select(transport => transport.Invoke()).ToList();
        _acceptLoops = new List<Task>(_transports.Count);
        _acceptedConnections = Channel.CreateBounded<HttpConnection>(new BoundedChannelOptions(options.BacklogCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _disposeCancellationTokenSource = new CancellationTokenSource();
        _acceptLoopLock = new Lock();
        Protocols = _transports.Aggregate(HttpProtocol.None, static (current, registration) => current | registration.HttpProtocols);
    }

    /// <summary>
    /// Gets the configured HTTP protocols supported by this listener.
    /// </summary>
    public HttpProtocol Protocols { get; }

    /// <inheritdoc />
    public override TransportProtocol Protocol => TransportProtocol.Http;

    /// <inheritdoc />
    public override async Task<HttpConnection> AcceptOrListenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(HttpConnectionListener));

        if (_transports.Count == 0)
        {
            throw new InvalidOperationException("At least one transport must be configured before accepting HTTP connections.");
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
    public override async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _disposeCancellationTokenSource.Cancel();

        foreach (HttpConnectionTransport transport in _transports)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
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

    private void EnsureAcceptLoopsStarted()
    {
        lock (_acceptLoopLock)
        {
            if (_acceptLoopsStarted)
            {
                return;
            }

            foreach (HttpConnectionTransport registration in _transports)
            {
                _acceptLoops.Add(RunAcceptLoopAsync(registration));
            }

            _acceptLoopsStarted = true;
        }
    }

    async Task<IHttpConnection> IHttpConnectionListener.AcceptOrListenAsync(CancellationToken cancellationToken)
    {
        return await AcceptOrListenAsync(cancellationToken);
    }

    private async Task RunAcceptLoopAsync(HttpConnectionTransport httpTransport)
    {
        try
        {
            while (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                ITransport transport = httpTransport;
                ITransportConnection transportConnection = await transport
                    .InitializeAsync(_disposeCancellationTokenSource.Token)
                    .ConfigureAwait(false);

                HttpConnection connection = httpTransport.HttpProtocols switch
                {
                    HttpProtocol.Http11 when transportConnection is ISingleStreamTransportConnection singleStreamConnection =>
                        new Http1Connection(singleStreamConnection, httpTransport.IsSecure),

                    HttpProtocol.Http20 when transportConnection is ISingleStreamTransportConnection singleStreamConnection =>
                        new Http2Connection(singleStreamConnection, httpTransport.IsSecure),

                    HttpProtocol.Http30 when transportConnection is IMultiplexTransportConnection multiplexTransportConnection =>
                        CreateHttp3Connection(multiplexTransportConnection, httpTransport.IsSecure),

                    HttpProtocol.Http11 or HttpProtocol.Http20 =>
                        throw new InvalidOperationException("HTTP/1.1 and HTTP/2 require a single-stream transport connection."),

                    HttpProtocol.Http30 =>
                        throw new InvalidOperationException("HTTP/3 requires a multiplexed transport connection."),

                    _ =>
                        throw new InvalidOperationException($"The configured HTTP protocol '{httpTransport.Protocol}' is not supported.")
                };

                await _acceptedConnections.Writer.WriteAsync(connection, _disposeCancellationTokenSource.Token).ConfigureAwait(false);
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
            _disposeCancellationTokenSource.Cancel();
            _acceptedConnections.Writer.TryComplete(exception);
        }
    }


    private static Http3Connection CreateHttp3Connection(IMultiplexTransportConnection connection, bool isSecure)
    {
        if (!IsHttp3SupportedPlatform())
        {
            throw new PlatformNotSupportedException("HTTP/3 transports require a QUIC-capable platform.");
        }

        return new Http3Connection(connection, isSecure);
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
