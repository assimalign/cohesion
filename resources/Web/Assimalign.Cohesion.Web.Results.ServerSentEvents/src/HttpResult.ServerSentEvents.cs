using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A Server-Sent Events result: streams a sequence of <see cref="ServerSentEvent"/> messages as a
/// <c>text/event-stream</c> response body (WHATWG HTML Server-Sent Events), flushing each event so
/// the client observes it immediately.
/// </summary>
/// <remarks>
/// <para>
/// Created through <c>Results.ServerSentEvents(...)</c> or <c>TypedResults.ServerSentEvents(...)</c>
/// (static extension factories in <see cref="ResultsServerSentEventsExtensions"/>); the constructor
/// is internal so the factories remain the only entry point.
/// </para>
/// <para>
/// The result is a thin adapter over the response-streaming feature (#769): it resolves
/// <see cref="IHttpResponseStreamingFeature"/> from <see cref="IHttpContext.Features"/> and
/// <em>fails loudly</em> with <see cref="NotSupportedException"/> when streaming is not enabled on
/// the exchange — it never silently buffers. Before the first write it sets
/// <c>Content-Type: text/event-stream</c> and <c>Cache-Control: no-cache</c> (an event stream must
/// not be cached); it never sets <c>Content-Length</c>, because the body length is unknowable and
/// framing is the transport's concern. Each event is written through the shipped
/// <c>WriteEventAsync</c> extension, which flushes per event.
/// </para>
/// <para>
/// <b>Single-use in practice.</b> The carrier itself is immutable, but the event sequence is
/// enumerated once per execution — reuse is only meaningful when the
/// <see cref="IAsyncEnumerable{T}"/> can be re-enumerated.
/// </para>
/// </remarks>
public sealed class ServerSentEventsHttpResult : IResult
{
    private readonly IAsyncEnumerable<ServerSentEvent> _events;

    internal ServerSentEventsHttpResult(IAsyncEnumerable<ServerSentEvent> events)
    {
        _events = events;
    }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets: <c>text/event-stream</c>.
    /// </summary>
    public string ContentType => ServerSentEvent.MediaType;

    /// <summary>
    /// Resolves the streaming feature, sets the event-stream headers, then writes and flushes each
    /// event in sequence and completes the response.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the enumeration and the writes.</param>
    /// <returns>A task that completes when the sequence has ended and the response is completed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// Response streaming is not enabled for this exchange (the streaming interceptor was not
    /// registered on the transport).
    /// </exception>
    public async Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Fails loudly (NotSupportedException) when the streaming interceptor is absent.
        IHttpResponseStreamingFeature streaming = context.Response.Streaming;

        // Headers lock when the response starts, so both must be set before the first write.
        context.Response.Headers[HttpHeaderKey.ContentType] = ServerSentEvent.MediaType;
        context.Response.Headers[HttpHeaderKey.CacheControl] = "no-cache";

        await foreach (ServerSentEvent @event in _events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await streaming.WriteEventAsync(@event, cancellationToken).ConfigureAwait(false);
        }

        await streaming.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
