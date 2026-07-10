using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A streaming result: hands the exchange's <see cref="IHttpResponseStreamingFeature"/> to a
/// caller-supplied callback so the body can be produced incrementally, then completes the response.
/// </summary>
/// <remarks>
/// <para>
/// Created through <see cref="Results.PushStream"/> or <see cref="TypedResults.PushStream"/>; the
/// constructor is internal so the factories remain the only entry point.
/// </para>
/// <para>
/// The result is a thin adapter over the response-streaming feature (#769): it resolves
/// <see cref="IHttpResponseStreamingFeature"/> from <see cref="IHttpContext.Features"/> and
/// <em>fails loudly</em> with <see cref="NotSupportedException"/> when streaming is not enabled on
/// the exchange — it never silently buffers. It never sets <c>Content-Length</c>: framing
/// (HTTP/1.1 chunking, h2/h3 DATA frames) is the transport's concern. Status code and
/// <c>Content-Type</c> are applied <em>before</em> the callback runs, because the response head is
/// committed and locked at the first write.
/// </para>
/// </remarks>
public sealed class PushStreamHttpResult : IResult
{
    private readonly Func<IHttpResponseStreamingFeature, CancellationToken, Task> _callback;

    internal PushStreamHttpResult(
        Func<IHttpResponseStreamingFeature, CancellationToken, Task> callback,
        string? contentType,
        HttpStatusCode? statusCode)
    {
        _callback = callback;
        ContentType = contentType;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets before the first write, or
    /// <see langword="null"/> to leave the header to the callback (set it before the first write —
    /// headers lock when the response starts).
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// Gets the status code this result sets before the first write, or <see langword="null"/> to
    /// leave the response's current status untouched.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Resolves the streaming feature, applies <see cref="StatusCode"/> and
    /// <see cref="ContentType"/>, runs the callback to produce the body incrementally, and
    /// completes the response.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the body writes.</param>
    /// <returns>A task that completes when the callback has run and the response is completed.</returns>
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

        if (StatusCode is HttpStatusCode status)
        {
            context.Response.StatusCode = status;
        }

        if (ContentType is not null)
        {
            context.Response.Headers[HttpHeaderKey.ContentType] = ContentType;
        }

        await _callback.Invoke(streaming, cancellationToken).ConfigureAwait(false);
        await streaming.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
