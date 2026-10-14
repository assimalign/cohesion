using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Connections.Internal;

/// <summary>
/// An <see cref="IDuplexConnectionFactory"/> that applies a <see cref="IConnectionLayer"/> to every
/// established connection.
/// </summary>
internal sealed class LayeredConnectionFactory : DuplexConnectionFactory
{
    private readonly IDuplexConnectionFactory _inner;
    private readonly IConnectionLayer _layer;

    public LayeredConnectionFactory(IDuplexConnectionFactory inner, IConnectionLayer layer)
    {
        _inner = inner;
        _layer = layer;
    }

    public override ConnectionCapabilities Capabilities => _layer.Describe(_inner.Capabilities);

    protected override async ValueTask<IDuplexConnection> ConnectCoreAsync(EndPoint endPoint, CancellationToken cancellationToken = default)
    {
        IDuplexConnection connection = await _inner.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);

        try
        {
            return await _layer.UpgradeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A connection whose upgrade failed still belongs to the caller (IConnectionLayer.UpgradeAsync),
            // and nothing else holds it: a TLS layer that fails disposes only its own stream (#1309).
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
