using System;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.Internal;

namespace Assimalign.Cohesion.Connections.Quic;

/// <summary>
/// Establishes outbound QUIC connections and surfaces each as a <see cref="QuicMultiplexedConnection"/>.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class QuicConnectionFactory : MultiplexedConnectionFactory
{
    private readonly QuicConnectionFactoryOptions _options;

    /// <summary>
    /// Creates a new QUIC connection factory with default options.
    /// </summary>
    public QuicConnectionFactory()
        : this(QuicConnectionFactoryOptions.Default)
    {
    }

    /// <summary>
    /// Creates a new QUIC connection factory.
    /// </summary>
    /// <param name="options">The QUIC client options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public QuicConnectionFactory(QuicConnectionFactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public override ConnectionCapabilities Capabilities { get; } = new ConnectionCapabilities(
        ConnectionProtocol.Quic,
        ConnectionDelivery.Stream,
        IsReliable: true,
        IsOrdered: true,
        IsMultiplexed: true,
        ConnectionSecurity.Tls);

    /// <inheritdoc />
    /// <remarks>
    /// The supplied <paramref name="endPoint"/> always wins over
    /// <see cref="QuicConnectionFactoryOptions.EndPoint"/>, which is only a configured default.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endPoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when QUIC is not supported on the current platform.</exception>
    public override async ValueTask<MultiplexedConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endPoint);

        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not supported on the current platform.");
        }

        var connectionOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = endPoint,
            ClientAuthenticationOptions = _options.ClientAuthenticationOptions,
            DefaultStreamErrorCode = _options.DefaultStreamErrorCode,
            DefaultCloseErrorCode = _options.DefaultCloseErrorCode
        };

        QuicConnection connection = await QuicConnection.ConnectAsync(connectionOptions, cancellationToken).ConfigureAwait(false);
        StreamPipeOptionsContext streamOptions = _options.CreateStreamOptions();

        try
        {
            // Factory-dialed connections carry no listener id; client connections are identified
            // by their ConnectionId and remote endpoint.
            return new QuicMultiplexedConnection(
                connection,
                default,
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

    /// <summary>
    /// Creates a QUIC connection factory using a configure callback.
    /// </summary>
    /// <param name="configure">The callback used to configure options.</param>
    /// <returns>A configured <see cref="QuicConnectionFactory"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is <see langword="null"/>.</exception>
    public static QuicConnectionFactory Create(Action<QuicConnectionFactoryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        QuicConnectionFactoryOptions options = new QuicConnectionFactoryOptions();

        configure(options);

        return new QuicConnectionFactory(options);
    }
}
