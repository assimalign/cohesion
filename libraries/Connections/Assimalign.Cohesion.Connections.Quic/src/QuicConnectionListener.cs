using System;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Quic.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// Listens for inbound QUIC connections and surfaces each as a <see cref="QuicMultiplexedConnection"/>.
/// </summary>
/// <remarks>
/// Constructing a listener does not acquire its endpoint. Call <see cref="BindAsync(CancellationToken)"/>
/// explicitly, or use <see cref="CreateAsync(QuicConnectionListenerOptions, CancellationToken)"/> as a
/// compatibility convenience that constructs and binds in one operation.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnectionListener : MultiplexedConnectionListener
{
    private readonly QuicConnectionListenerOptions _options;
    private readonly ListenerId _listenerId = ListenerId.New();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private QuicListener? _listener;
    private bool _isDisposed;

    /// <summary>
    /// Initializes an unbound QUIC connection listener.
    /// </summary>
    /// <param name="options">The QUIC server options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public QuicConnectionListener(QuicConnectionListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Before the listener is bound this is the configured endpoint; afterwards it is the actual
    /// local endpoint of the QUIC listener (relevant when binding to port 0).
    /// </remarks>
    public override EndPoint EndPoint => _listener?.LocalEndPoint ?? _options.EndPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: true,
        ConnectionSecurity.Tls);

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when QUIC is not supported on the current platform.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no server certificate or no ALPN application protocol is configured.
    /// </exception>
    public override async ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_listener is not null)
            {
                return;
            }

            if (!QuicListener.IsSupported)
            {
                throw new PlatformNotSupportedException("QUIC is not supported on the current platform.");
            }

            ValidateServerAuthenticationOptions(_options);

            _listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = _options.EndPoint,
                ApplicationProtocols = _options.ServerAuthenticationOptions.ApplicationProtocols!,
                ListenBacklog = _options.Backlog,
                ConnectionOptionsCallback = (connection, sslClientHelloInfo, token) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    ServerAuthenticationOptions = _options.ServerAuthenticationOptions,
                    MaxInboundBidirectionalStreams = _options.MaxBidirectionalStreamCount,
                    MaxInboundUnidirectionalStreams = _options.MaxUnidirectionalStreamCount,
                    DefaultCloseErrorCode = _options.DefaultCloseErrorCode,
                    DefaultStreamErrorCode = _options.DefaultStreamErrorCode
                })
            }, cancellationToken).ConfigureAwait(false);

            QuicConnectionEventSource.Log.ListenerBound(_listenerId, _listener.LocalEndPoint);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Creates a QUIC connection listener bound to the endpoint configured on <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The QUIC server options.</param>
    /// <param name="cancellationToken">A token to cancel the listen operation.</param>
    /// <returns>The created <see cref="QuicConnectionListener"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when QUIC is not supported on the current platform.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no server certificate or no ALPN application protocol is configured.
    /// </exception>
    public static async ValueTask<QuicConnectionListener> CreateAsync(QuicConnectionListenerOptions options, CancellationToken cancellationToken = default)
    {
        QuicConnectionListener listener = new(options);
        await listener.BindAsync(cancellationToken).ConfigureAwait(false);

        return listener;
    }

    /// <summary>
    /// Creates a QUIC connection listener using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <param name="cancellationToken">A token to cancel the listen operation.</param>
    /// <returns>The created <see cref="QuicConnectionListener"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when QUIC is not supported on the current platform.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no server certificate or no ALPN application protocol is configured.
    /// </exception>
    public static ValueTask<QuicConnectionListener> CreateAsync(Action<QuicConnectionListenerOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        QuicConnectionListenerOptions options = new QuicConnectionListenerOptions();

        configure(options);

        return CreateAsync(options, cancellationToken);
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    public override async ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        await BindAsync(cancellationToken).ConfigureAwait(false);

        QuicListener listener;

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            listener = _listener!;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        QuicConnection connection = await listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
        StreamPipeOptionsContext streamOptions = _options.CreateStreamOptions();

        try
        {
            return new QuicMultiplexedConnection(
                connection,
                _listenerId,
                _options.DefaultStreamErrorCode,
                _options.DefaultCloseErrorCode,
                streamOptions);
        }
        catch
        {
            streamOptions.Dispose();
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        QuicListener? listener;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            listener = _listener;
            _listener = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (listener is not null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);

            QuicConnectionEventSource.Log.ListenerClosed(_listenerId);
        }
    }

    private static void ValidateServerAuthenticationOptions(QuicConnectionListenerOptions options)
    {
        if (options.ServerAuthenticationOptions.ServerCertificate is null &&
            options.ServerAuthenticationOptions.ServerCertificateContext is null &&
            options.ServerAuthenticationOptions.ServerCertificateSelectionCallback is null)
        {
            throw new InvalidOperationException("A server certificate is required for QUIC server authentication.");
        }

        if (options.ServerAuthenticationOptions.ApplicationProtocols is null ||
            options.ServerAuthenticationOptions.ApplicationProtocols.Count == 0)
        {
            throw new InvalidOperationException("At least one application protocol is required for QUIC server authentication.");
        }
    }
}
