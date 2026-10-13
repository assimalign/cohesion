using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections;

/// <summary>Establishes connections of a declared kind through the same operation as its untyped view.</summary>
/// <typeparam name="TConnection">The kind of connection the factory produces.</typeparam>
public interface IConnectionFactory<TConnection> : IConnectionFactory where TConnection : IConnection
{
    /// <summary>Establishes a connection to the specified remote endpoint.</summary>
    /// <param name="endPoint">The remote endpoint to connect to.</param>
    /// <param name="cancellationToken">A token that cancels connection establishment.</param>
    /// <returns>The established connection, owned by the caller.</returns>
    new ValueTask<TConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default);
}
