using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A typed feature that lets an application <em>start</em> a response and write its body
/// <em>incrementally</em>, flushing bytes to the transport as they are produced instead of
/// buffering the whole body and sending it in one shot.
/// </summary>
/// <remarks>
/// <para>
/// This is a response feature package, not part of the protocol core or the transport. It is made
/// available on an exchange by registering its response interceptor
/// (<see cref="HttpResponseStreaming.CreateInterceptor"/>) on the server transport's
/// <c>Interceptors</c> list; the interceptor wraps the transport's raw response body sink and
/// installs this feature, so the transport never depends on this package. A handler resolves it via
/// <c>context.Features.Get&lt;IHttpResponseStreamingFeature&gt;()</c> or the ergonomic
/// <c>context.Response.Streaming</c> accessor.
/// </para>
/// <para>
/// <b>Header-commit timing (the load-bearing rule).</b> The response status line and header block
/// are committed to the wire exactly once, at the moment the response <em>starts</em> — the first of
/// <see cref="StartAsync"/>, <see cref="WriteAsync"/>, <see cref="FlushAsync"/>, or
/// <see cref="CompleteAsync"/> to run. After the response has started (<see cref="HasStarted"/> is
/// <see langword="true"/>) the status code and headers are <em>locked</em>: they have already been
/// transmitted, so further mutation has no effect on the wire. Set every header before the first
/// call.
/// </para>
/// <para>
/// <b>Framing and backpressure</b> are the transport's concern: HTTP/1.1 uses chunked transfer
/// coding when no <c>Content-Length</c> was set; HTTP/2 and HTTP/3 emit incremental <c>DATA</c>
/// frames. Writes respect transport flow control and therefore <em>await</em> available send credit
/// rather than buffering unbounded. The wire terminator is emitted when the transport finalizes the
/// exchange; <see cref="CompleteAsync"/> flushes and forbids further writes.
/// </para>
/// <para>
/// <b>Threading.</b> Like <see cref="System.IO.Stream"/>, an implementation is <em>not</em> safe for
/// concurrent use: a single exchange's response is written by one logical caller.
/// </para>
/// </remarks>
public interface IHttpResponseStreamingFeature : IHttpFeature
{
    /// <summary>
    /// Gets a value indicating whether the response has started — that is, whether the status line
    /// and headers have been committed to the wire. Once <see langword="true"/>, the status code and
    /// headers are locked.
    /// </summary>
    bool HasStarted { get; }

    /// <summary>
    /// Starts the response by committing the status line and header block to the transport, locking
    /// the status code and headers. Idempotent.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the header write.</param>
    /// <returns>A task that completes when the head has been flushed to the transport.</returns>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a chunk of response-body bytes, starting the response first if it has not started. The
    /// write awaits transport flow-control credit rather than buffering unbounded.
    /// </summary>
    /// <param name="data">The body bytes to write. An empty buffer is a no-op beyond starting the response.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the bytes have been handed to the transport.</returns>
    /// <exception cref="InvalidOperationException">The response has already been completed.</exception>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes any buffered body bytes through to the transport so the peer can observe them,
    /// starting the response first if it has not started.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the flush.</param>
    /// <returns>A task that completes when the buffered bytes have been flushed.</returns>
    /// <exception cref="InvalidOperationException">The response has already been completed.</exception>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the response body: flushes and forbids further writes. Idempotent. Starts the
    /// response first if it never started — so calling only <see cref="CompleteAsync"/> still commits
    /// the head and locks the headers (an empty streamed response). The transport emits the wire
    /// end-of-body marker (terminating zero-length chunk / <c>END_STREAM</c>) when it finalizes the
    /// exchange.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the completion.</param>
    /// <returns>A task that completes when the body has been flushed.</returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}
