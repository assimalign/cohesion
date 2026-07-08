using System;
using System.IO;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// HTTP/1.1 implementation of the <see cref="IHttpConnectionTakeover"/> capability, offered to
/// response interceptors through <see cref="HttpResponseInterceptorContext.ConnectionTakeover"/>.
/// An HTTP/1.1 exchange owns its whole connection, so taking it over is legal; the multiplexed
/// transports never construct one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TakeOver"/> flips the owning <see cref="Http1Context"/> into its finalized state
/// <em>before</em> surrendering the stream: <see cref="Http1Context.ResponseFinalized"/> makes
/// <see cref="Http1ConnectionContext.SendAsync"/> a no-op (nothing is double-written onto the raw
/// stream), and clearing <see cref="Http1Context.KeepAlive"/> ends the connection's request loop
/// after the current exchange (the parser never mistakes post-transition octets for a next
/// request). The handover is safe because the HTTP/1.1 parser reads byte-by-byte and never
/// buffers past the request it parsed — the surrendered stream starts exactly at the first
/// post-request octet.
/// </para>
/// <para>
/// The capability is one-shot (a second <see cref="TakeOver"/> throws) so two features cannot
/// both claim the connection. The transport retains ownership of the underlying connection's
/// disposal — the caller owns I/O on the stream, not the socket lifetime.
/// </para>
/// </remarks>
internal sealed class Http1ConnectionTakeover : IHttpConnectionTakeover
{
    private readonly Http1Context _context;
    private readonly Stream _stream;
    private int _takenOver;

    public Http1ConnectionTakeover(Http1Context context, Stream stream)
    {
        _context = context;
        _stream = stream;
    }

    /// <inheritdoc />
    public Stream TakeOver()
    {
        if (Interlocked.Exchange(ref _takenOver, 1) == 1)
        {
            throw new InvalidOperationException(
                "The connection has already been taken over for this exchange.");
        }

        _context.ResponseFinalized = true;
        _context.KeepAlive = false;
        return _stream;
    }
}
