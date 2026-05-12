using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Http.Transports.Internal.Http2;

internal sealed class Http2Connection : HttpConnection
{
    private readonly ISingleStreamTransportConnection _connection;
    private Http2ConnectionContext? _context;

    public Http2Connection(ISingleStreamTransportConnection connection, bool isSecure)
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
        if (_context is not null)
        {
            return _context;
        }

        ITransportConnectionContext context = await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        _context = new Http2ConnectionContext(context, IsSecure);

        return _context;
    }

    public override ValueTask DisposeAsync()
    {
        return Connection.DisposeAsync();
    }
}
