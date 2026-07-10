using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A file result over a caller-supplied <see cref="Stream"/>: copies the stream to the response
/// body with an explicit <c>Content-Type</c>, setting <c>Content-Length</c> when the stream's
/// remaining length is knowable (seekable streams).
/// </summary>
/// <remarks>
/// <para>
/// Created through <see cref="Results.FileStream"/> or <see cref="TypedResults.FileStream"/>; the
/// constructor is internal so the factories remain the only entry point. This is the
/// <em>unconditional</em> variant: range requests and preconditions are deferred to the
/// precondition-aware file result (#777).
/// </para>
/// <para>
/// <b>Single-use.</b> The result takes ownership of the stream and disposes it after the copy, so
/// an instance cannot be executed twice — unlike the other built-ins, which are immutable and
/// reusable.
/// </para>
/// </remarks>
public sealed class FileStreamHttpResult : IResult
{
    internal FileStreamHttpResult(Stream stream, string? contentType)
    {
        Stream = stream;
        ContentType = contentType ?? HttpContentTypes.Fallback;
    }

    /// <summary>
    /// Gets the stream copied to the response body. Owned by the result: it is disposed when
    /// execution completes.
    /// </summary>
    public Stream Stream { get; }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets. Defaults to
    /// <see cref="HttpContentTypes.Fallback"/> (<c>application/octet-stream</c>) when the factory
    /// was given none.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Copies <see cref="Stream"/> to the response body with <see cref="ContentType"/>, setting
    /// <c>Content-Length</c> to the remaining length when the stream is seekable, then disposes the
    /// stream. The status code is left untouched.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the copy.</param>
    /// <returns>A task that completes when the body has been written and the stream disposed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public async Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        Stream source = Stream;
        await using (source.ConfigureAwait(false))
        {
            IHttpResponse response = context.Response;

            response.Headers[HttpHeaderKey.ContentType] = ContentType;

            if (source.CanSeek)
            {
                long remaining = source.Length - source.Position;
                response.Headers[HttpHeaderKey.ContentLength] = remaining.ToString(CultureInfo.InvariantCulture);
            }

            await source.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
    }
}
