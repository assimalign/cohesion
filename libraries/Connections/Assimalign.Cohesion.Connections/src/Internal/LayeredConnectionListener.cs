using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// An <see cref="IConnectionListener"/> that applies a <see cref="IConnectionLayer"/> to every
/// accepted connection.
/// </summary>
internal sealed class LayeredConnectionListener : IConnectionListener
{
    private readonly IConnectionListener _inner;
    private readonly IConnectionLayer _layer;

    public LayeredConnectionListener(IConnectionListener inner, IConnectionLayer layer)
    {
        _inner = inner;
        _layer = layer;
    }

    public EndPoint EndPoint => _inner.EndPoint;

    public ConnectionCapabilities Capabilities => _layer.Describe(_inner.Capabilities);

    public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        IConnection connection = await _inner.AcceptAsync(cancellationToken).ConfigureAwait(false);

        return await _layer.UpgradeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
