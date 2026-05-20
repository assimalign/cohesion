using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

using Assimalign.Cohesion.Http.Transports.Internal;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports;

/// <summary>
/// Accepts transport connections and adapts them into HTTP protocol connections.
/// </summary>
public sealed class HttpConnectionListener : ServerTransport<HttpConnection>, IHttpConnectionListener
{
    private readonly HttpConnectionFactory _factory;
    private readonly List<HttpProtocolRegistration> _registrations;
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

        _factory = new HttpConnectionFactory();
        _registrations = new List<HttpProtocolRegistration>(options.GetRegistrations());
        _acceptLoops = new List<Task>(_registrations.Count);
        _acceptedConnections = Channel.CreateBounded<HttpConnection>(new BoundedChannelOptions(options.BacklogCapacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _disposeCancellationTokenSource = new CancellationTokenSource();
        _acceptLoopLock = new Lock();
        Protocols = options.Protocols;
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

        if (_registrations.Count == 0)
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

        foreach (HttpProtocolRegistration registration in _registrations)
        {
            await registration.Transport.DisposeAsync().ConfigureAwait(false);
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

            foreach (HttpProtocolRegistration registration in _registrations)
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

    private async Task RunAcceptLoopAsync(HttpProtocolRegistration registration)
    {
        try
        {
            while (!_disposeCancellationTokenSource.IsCancellationRequested)
            {
                ITransport transport = registration.Transport;
                ITransportConnection transportConnection = await transport.InitializeAsync(_disposeCancellationTokenSource.Token).ConfigureAwait(false);
                HttpConnection connection = _factory.Create(registration, transportConnection);

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
}
