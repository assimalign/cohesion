using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http1;

internal sealed class Http1Connection : HttpConnection
{
    private readonly ISingleStreamTransportConnection _connection;
    private Http1ConnectionContext? _openContext;

    public Http1Connection(ISingleStreamTransportConnection connection, bool isSecure)
        : base(connection, isSecure)
    {
        _connection = connection;
    }

    public override HttpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public override async ValueTask<HttpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_openContext is not null)
        {
            return _openContext;
        }

        ITransportConnectionContext transportContext = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        _openContext = new Http1ConnectionContext(transportContext, IsSecure);

        return _openContext;
    }

    public override ValueTask DisposeAsync()
    {
        return Connection.DisposeAsync();
    }
}
