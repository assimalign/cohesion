using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections;

/// <summary>
/// Represents the active HTTP connection context used to receive exchanges and write responses.
/// </summary>
public interface IHttpConnectionContext
{
    /// <summary>
    /// Gets the local endpoint the underlying connection is bound to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the remote endpoint the underlying connection is connected to, or <see langword="null"/> when not applicable.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Receives HTTP exchanges from the underlying connection.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for enumeration.</param>
    /// <returns>An asynchronous sequence of received HTTP contexts.</returns>
    /// <remarks>
    /// Every exchange this yields must be finalized with <see cref="SendAsync"/> or disposed, also after
    /// its <see cref="IHttpContext.RequestCancelled"/> fired. On HTTP/2 the exchange keeps its stream's
    /// slot against <c>SETTINGS_MAX_CONCURRENT_STREAMS</c> until then, even once the stream was reset
    /// (RFC 9113 §5.1.2): a handler that ignores cancellation is still work in flight. An exchange the
    /// host drops without either never gives its slot back, and once
    /// <see cref="Http2ConnectionListenerOptions.Http2Limits.MaxStreamsPerConnection"/> such slots are
    /// held, the connection refuses every new stream for the rest of its life.
    /// </remarks>
    IAsyncEnumerable<IHttpContext> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the response state contained by the supplied HTTP context to the underlying connection.
    /// </summary>
    /// <param name="context">The HTTP context to serialize back to the client.</param>
    /// <param name="cancellationToken">The cancellation token for the write operation.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <remarks>
    /// Cancelling <paramref name="cancellationToken"/> once the final response is claimed abandons it,
    /// and the exchange can carry no other response. On HTTP/2 the transport then resets the stream
    /// with <c>RST_STREAM(CANCEL)</c> before the <see cref="System.OperationCanceledException"/>
    /// propagates, so the stream does not stay open holding its slot and the connection's graceful
    /// close does not wait for it; that applies to a buffered response, a streamed one, and an extended
    /// CONNECT tunnel's end alike. A response whose <c>END_STREAM</c> already reached the transport is
    /// complete, so its stream is ended without a reset and only the exception reports the
    /// cancellation. The call still ends the exchange (see <see cref="ReceiveAsync"/>), whether it
    /// returns or throws.
    /// </remarks>
    ValueTask SendAsync(IHttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a graceful close: the connection takes no new exchange and tells its peer so, while every
    /// exchange <see cref="ReceiveAsync"/> has already yielded runs to completion.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The call returns at once. Each protocol announces the close the way its specification asks, and
    /// any frame that takes is written in the background; the connection's own disposal waits for it.
    /// </para>
    /// <list type="bullet">
    /// <item><description>HTTP/1.1 (RFC 9112 §9.6): the response to the exchange in flight carries
    /// <c>Connection: close</c> and the connection ends after it. An idle keep-alive connection, one
    /// still waiting for the first octet of its next request, ends at once without a response. A
    /// request whose head has started to arrive is still read and answered, with
    /// <c>Connection: close</c>.</description></item>
    /// <item><description>HTTP/2 (RFC 9113 §6.8): a <c>GOAWAY(NO_ERROR)</c> carrying the highest stream
    /// the connection accepted. A stream opened afterwards, or one whose header block was still
    /// arriving, is refused with <c>RST_STREAM(REFUSED_STREAM)</c>, which tells the peer it was not
    /// processed.</description></item>
    /// <item><description>HTTP/3 (RFC 9114 §5.2): the connection stops accepting request streams and
    /// sends a <c>GOAWAY</c> carrying the first stream it did not accept. A request whose head was still
    /// arriving is reset with <c>H3_REQUEST_REJECTED</c>.</description></item>
    /// </list>
    /// <para>
    /// <see cref="ReceiveAsync"/> then ends on its own once no further exchange can arrive. Nothing it
    /// has yielded is cancelled: those exchanges keep reading their request bodies and are still sent
    /// through <see cref="SendAsync"/>. A host that cannot wait for them any longer cancels the token it
    /// enumerates <see cref="ReceiveAsync"/> with — every exchange the connection yielded then observes
    /// <see cref="IHttpContext.RequestCancelled"/> — and aborts the connection
    /// (<see cref="IHttpConnection.Abort"/>).
    /// </para>
    /// <para>
    /// Safe to call from any thread, including while <see cref="ReceiveAsync"/> is being enumerated and
    /// while exchanges are being sent. Calls after the first do nothing.
    /// </para>
    /// </remarks>
    void BeginGracefulClose();
}
