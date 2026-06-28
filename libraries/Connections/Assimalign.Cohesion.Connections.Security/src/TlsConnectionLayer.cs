using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.Security.Internal;

namespace Assimalign.Cohesion.Connections.Security;

/// <summary>
/// A <see cref="IConnectionLayer"/> that secures connections with TLS.
/// </summary>
/// <remarks>
/// Construct with <see cref="TlsServerOptions"/> to authenticate as the server (compose onto an
/// <c>IConnectionListener</c>), or with <see cref="TlsClientOptions"/> to authenticate as the
/// client (compose onto an <c>IConnectionFactory</c>).
/// </remarks>
public sealed class TlsConnectionLayer : IConnectionLayer
{
    private readonly TlsServerOptions? _serverOptions;
    private readonly TlsClientOptions? _clientOptions;

    /// <summary>
    /// Initializes a server-side TLS layer.
    /// </summary>
    /// <param name="options">The server TLS options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public TlsConnectionLayer(TlsServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _serverOptions = options;
    }

    /// <summary>
    /// Initializes a client-side TLS layer.
    /// </summary>
    /// <param name="options">The client TLS options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    public TlsConnectionLayer(TlsClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clientOptions = options;
    }

    /// <inheritdoc />
    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities)
        => capabilities with { Security = ConnectionSecurity.Tls };

    /// <inheritdoc />
    public ValueTask<IConnection> UpgradeAsync(IConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return _serverOptions is not null
            ? TlsConnection.AuthenticateAsServerAsync(connection, _serverOptions, cancellationToken)
            : TlsConnection.AuthenticateAsClientAsync(connection, _clientOptions!, cancellationToken);
    }
}
