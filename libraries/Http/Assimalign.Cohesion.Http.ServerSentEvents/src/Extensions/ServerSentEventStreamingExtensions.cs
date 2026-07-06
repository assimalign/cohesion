using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Bridges the <see cref="ServerSentEvent"/> value model onto the
/// <see cref="IHttpResponseStreamingFeature"/> write/flush seam, so a handler can
/// emit Server-Sent Events over any transport that supports response streaming.
/// </summary>
/// <remarks>
/// The caller is responsible for setting <c>Content-Type: text/event-stream</c>
/// (see <see cref="ServerSentEvent.MediaType"/>) on the response before the first
/// write, since headers are locked once the response starts. Each write serializes
/// the event with <see cref="ServerSentEventFormatter"/> and flushes it so the peer
/// observes it immediately.
/// </remarks>
public static class ServerSentEventStreamingExtensions
{
    extension(IHttpResponseStreamingFeature streaming)
    {
        /// <summary>
        /// Serializes <paramref name="event"/> to the <c>text/event-stream</c>
        /// wire format and writes it to the response, flushing so the peer
        /// observes it immediately.
        /// </summary>
        /// <param name="event">The event to send.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>A task that completes when the event has been flushed to the transport.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="streaming"/> is <see langword="null"/>.</exception>
        public async ValueTask WriteEventAsync(ServerSentEvent @event, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(streaming);

            ArrayBufferWriter<byte> buffer = new();
            ServerSentEventFormatter.Write(@event, buffer);
            await streaming.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await streaming.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes a keep-alive heartbeat — a comment-only event that a client
        /// ignores but whose bytes keep the idle stream (and any intermediaries)
        /// from timing out.
        /// </summary>
        /// <param name="comment">The comment text. Defaults to <c>keep-alive</c>.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        /// <returns>A task that completes when the heartbeat has been flushed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="streaming"/> is <see langword="null"/>.</exception>
        public ValueTask WriteKeepAliveAsync(string comment = "keep-alive", CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(streaming);
            return streaming.WriteEventAsync(ServerSentEvent.KeepAlive(comment), cancellationToken);
        }
    }
}
