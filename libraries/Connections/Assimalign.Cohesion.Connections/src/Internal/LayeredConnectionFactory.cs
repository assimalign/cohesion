using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// An <see cref="IConnectionFactory"/> that applies a <see cref="IConnectionLayer"/> to every
/// established connection.
/// </summary>
internal sealed class LayeredConnectionFactory : IConnectionFactory
{
    private readonly IConnectionFactory _inner;
    private readonly IConnectionLayer _layer;

    public LayeredConnectionFactory(IConnectionFactory inner, IConnectionLayer layer)
    {
        _inner = inner;
        _layer = layer;
    }

    public ConnectionCapabilities Capabilities => _layer.Describe(_inner.Capabilities);

    public async ValueTask<IConnection> ConnectAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        IConnection connection = await _inner.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        return await _layer.UpgradeAsync(connection, cancellationToken).ConfigureAwait(false);
    }
}
