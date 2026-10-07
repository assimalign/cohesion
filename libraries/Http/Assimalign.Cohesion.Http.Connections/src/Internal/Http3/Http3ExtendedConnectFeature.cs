using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/3 <see cref="IHttpExtendedConnectFeature"/> (RFC 9220). Accepting registers the tunnel on the
/// exchange and writes the <c>200</c> HEADERS frame on the request stream without ending it, through
/// the tunnel's own serialized writer (<see cref="Http3ExtendedConnectStream"/>).
/// </summary>
internal sealed class Http3ExtendedConnectFeature : HttpExtendedConnectFeature
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3Context _context;

    /// <summary>
    /// Initializes the feature for an HTTP/3 extended CONNECT exchange.
    /// </summary>
    /// <param name="connection">The connection the request stream belongs to.</param>
    /// <param name="context">The extended CONNECT exchange.</param>
    /// <param name="protocol">The <c>:protocol</c> the client requested.</param>
    public Http3ExtendedConnectFeature(Http3ConnectionContext connection, Http3Context context, string protocol)
        : base(context, protocol)
    {
        _connection = connection;
        _context = context;
    }

    /// <inheritdoc />
    protected override void ThrowIfStreamAborted()
    {
        if (_context.RequestBody.IsReset)
        {
            throw new IOException("The HTTP/3 request stream was reset before the extended CONNECT tunnel was accepted.");
        }

        if (_connection.ConnectionClosed.IsCancellationRequested)
        {
            throw new IOException("The HTTP/3 connection closed before the extended CONNECT tunnel was accepted.");
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<HttpExtendedConnectStream> AcceptCoreAsync(CancellationToken cancellationToken)
    {
        _context.MarkFinalResponseStarted();

        Http3ExtendedConnectStream tunnel = new(_connection, _context);
        _context.Tunnel = tunnel;

        await tunnel.WriteHeadAsync(Http3HeaderCodec.EncodeResponseHeaders(_context), cancellationToken).ConfigureAwait(false);
        tunnel.MarkHeadCommitted();

        return tunnel;
    }
}
