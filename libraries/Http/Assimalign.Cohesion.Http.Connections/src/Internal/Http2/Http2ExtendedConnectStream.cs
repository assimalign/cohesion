using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// HTTP/2 extended CONNECT tunnel (RFC 8441 §5). Reads drain the stream's request-body pipe, which the
/// frame pump fills with the peer's <c>DATA</c> and whose consumption credits the receive windows back
/// to the peer; writes and the end of the server's side go through the owning
/// <see cref="Http2ConnectionContext"/>, which owns the write gate and the send-window accounting.
/// </summary>
internal sealed class Http2ExtendedConnectStream : HttpExtendedConnectStream
{
    private readonly Http2ConnectionContext _connection;
    private readonly Http2Context _context;
    private readonly Http2RequestBodyStream _requestBody;

    /// <summary>
    /// Initializes the tunnel for an accepted HTTP/2 extended CONNECT.
    /// </summary>
    /// <param name="connection">The connection that writes the tunnel's frames.</param>
    /// <param name="context">The exchange whose stream carries the tunnel.</param>
    /// <param name="requestBody">The transport's request-body stream for the exchange.</param>
    public Http2ExtendedConnectStream(Http2ConnectionContext connection, Http2Context context, Http2RequestBodyStream requestBody)
    {
        _connection = connection;
        _context = context;
        _requestBody = requestBody;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The body pipe returns 0 only at the peer's END_STREAM: a reset or a torn-down connection fires
    /// the stream's abort and fails the pipe instead, which the base turns into an
    /// <see cref="IOException"/>.
    /// </remarks>
    protected override ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        => _requestBody.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask WriteCoreAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => _connection.WriteTunnelDataAsync(_context, data, cancellationToken);

    /// <inheritdoc />
    protected override ValueTask CloseCoreAsync()
        => _connection.CompleteTunnelAsync(_context);
}
