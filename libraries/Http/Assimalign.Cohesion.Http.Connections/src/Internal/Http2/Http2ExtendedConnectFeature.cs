using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/2 <see cref="IHttpExtendedConnectFeature"/> (RFC 8441). Accepting claims the stream's final
/// response, registers the tunnel on the exchange, and writes the <c>200</c> HEADERS block without
/// <c>END_STREAM</c> through <see cref="Http2ConnectionContext.WriteTunnelHeadAsync"/>, which holds the
/// connection write gate.
/// </summary>
internal sealed class Http2ExtendedConnectFeature : HttpExtendedConnectFeature
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;
    private readonly Http2RequestBodyStream _requestBody;

    /// <summary>
    /// Initializes the feature for an HTTP/2 extended CONNECT exchange.
    /// </summary>
    /// <param name="connection">The connection that writes the tunnel's frames.</param>
    /// <param name="context">The extended CONNECT exchange.</param>
    /// <param name="protocol">The <c>:protocol</c> the client requested.</param>
    /// <param name="requestBody">The transport's request-body stream, which carries the peer's tunnel octets.</param>
    public Http2ExtendedConnectFeature(
        Http2ConnectionContext connection,
        Http2Context context,
        string protocol,
        Http2RequestBodyStream requestBody)
        : base(context, protocol)
    {
        _connection = connection;
        _context = context;
        _requestBody = requestBody;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Also started once the stream's final response is claimed — by the transport's own <c>413</c>,
    /// which a CONNECT never draws since its octets are not a message body, but the claim rules it out
    /// regardless.
    /// </remarks>
    protected override bool HasResponseStarted => base.HasResponseStarted || _context.Stream.IsResponseClaimed;

    /// <inheritdoc />
    protected override void ThrowIfStreamAborted()
    {
        if (_context.Stream.IsReset)
        {
            throw new IOException($"The HTTP/2 stream {_context.StreamId} was reset before the extended CONNECT tunnel was accepted.");
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<HttpExtendedConnectStream> AcceptCoreAsync(CancellationToken cancellationToken)
    {
        // RFC 9113 §8.1 — the stream carries one final response, and from here it is the tunnel's.
        if (!_context.Stream.TryClaimResponse())
        {
            throw new InvalidOperationException("The extended CONNECT tunnel cannot be accepted after the response has started.");
        }

        _context.MarkFinalResponseStarted();

        Http2ExtendedConnectStream tunnel = new(_connection, _context, _requestBody);
        _context.Tunnel = tunnel;

        await _connection.WriteTunnelHeadAsync(_context, cancellationToken).ConfigureAwait(false);
        tunnel.MarkHeadCommitted();

        return tunnel;
    }
}
