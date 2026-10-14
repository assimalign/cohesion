using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>Provides one connection-establishment path for typed and untyped factory callers.</summary>
/// <typeparam name="TConnection">The kind of connection the factory produces.</typeparam>
public abstract class ConnectionFactory<TConnection> : IConnectionFactory<TConnection> where TConnection : IConnection
{
    /// <inheritdoc />
    public abstract ConnectionCapabilities Capabilities { get; }

    /// <inheritdoc />
    public ValueTask<TConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
        => ConnectCoreAsync(endPoint, cancellationToken);

    /// <summary>Establishes a connection and transfers ownership to the calling code.</summary>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A token that cancels connection establishment.</param>
    /// <returns>The established connection.</returns>
    protected abstract ValueTask<TConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default);

    async ValueTask<IConnection> IConnectionFactory.ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken)
        => await ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
}
