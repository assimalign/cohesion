using System;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// Listens for inbound QUIC connections and surfaces each as a <see cref="QuicMultiplexedConnection"/>.
/// </summary>
/// <remarks>
/// Binding a QUIC listener is inherently asynchronous, so instances are created through
/// <see cref="CreateAsync(QuicConnectionListenerOptions, CancellationToken)"/> rather than a constructor.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnectionListener : MultiplexedConnectionListener
{
    private readonly QuicListener _listener;
    private readonly QuicConnectionListenerOptions _options;
    private readonly ListenerId _listenerId = ListenerId.New();

    private bool _isDisposed;

    private QuicConnectionListener(QuicListener listener, QuicConnectionListenerOptions options)
    {
        _listener = listener;
        _options = options;

        ConnectionEventSource.Log.ListenerInitialized(ConnectionProtocol.Quic, _listenerId);
    }

    /// <inheritdoc />
    public override EndPoint EndPoint => _listener.LocalEndPoint;

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: true,
        ConnectionSecurity.Tls);

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
        ArgumentNullException.ThrowIfNull(options);

        if (!QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not supported on the current platform.");
        }

        ValidateServerAuthenticationOptions(options);

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = options.EndPoint,
            ApplicationProtocols = options.ServerAuthenticationOptions.ApplicationProtocols!,
            ListenBacklog = options.Backlog,
            ConnectionOptionsCallback = (connection, sslClientHelloInfo, token) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = options.ServerAuthenticationOptions,
                MaxInboundBidirectionalStreams = options.MaxBidirectionalStreamCount,
                MaxInboundUnidirectionalStreams = options.MaxUnidirectionalStreamCount,
                DefaultCloseErrorCode = options.DefaultCloseErrorCode,
                DefaultStreamErrorCode = options.DefaultStreamErrorCode
            })
        }, cancellationToken).ConfigureAwait(false);

        return new QuicConnectionListener(listener, options);
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
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        QuicConnection connection = await _listener.AcceptConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        await _listener.DisposeAsync().ConfigureAwait(false);
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
