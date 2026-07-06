using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http2;

/// <summary>
/// The seam through which a streaming HTTP/2 request body reports how many
/// flow-control octets the application has consumed, so the connection can
/// credit them back to the peer.
/// </summary>
/// <remarks>
/// RFC 9113 §5.2 makes flow control receiver-driven: the receiver paces the
/// sender by only crediting <c>WINDOW_UPDATE</c> as it consumes buffered DATA.
/// <see cref="Http2ConnectionContext"/> implements this seam; the body stream
/// calls it as the application drains each <see cref="Http2DataChunk"/>, which is
/// what turns application consumption — rather than mere receipt — into the
/// signal that resumes a stalled sender.
/// </remarks>
internal interface IHttp2RequestBodySink
{
    /// <summary>
    /// Credits <paramref name="flowControlLength"/> octets back to the stream's
    /// and the connection's receive windows and emits the paired
    /// <c>WINDOW_UPDATE</c> frames (RFC 9113 §6.9).
    /// </summary>
    /// <param name="streamId">The stream whose body was consumed.</param>
    /// <param name="flowControlLength">The flow-control octets to credit.</param>
    /// <param name="cancellationToken">A token to cancel the emission.</param>
    /// <returns>A task that completes once the credit has been emitted.</returns>
    ValueTask OnRequestBodyConsumedAsync(int streamId, int flowControlLength, CancellationToken cancellationToken);
}
