using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Answers a request with a selected representation: RFC 9110 &#167; 13 preconditions through
/// <see cref="HttpConditionalRequest"/>, &#167; 14 single byte ranges through
/// <see cref="HttpRangeSelector"/> gated by <see cref="HttpIfRange"/>, and the header section and
/// content of the outcome. This is the one implementation behind the static-files middleware,
/// <c>MapFallbackToFile</c>, and the <c>SendFileAsync</c>/<c>WriteStreamAsync</c> response helpers;
/// each caller describes its representation with <see cref="RepresentationMetadata"/> and supplies
/// the bytes, and this type decides between <c>200</c>, <c>206</c>, <c>304</c>, <c>412</c>, and
/// <c>416</c>.
/// </summary>
/// <remarks>
/// The work is split so a caller can defer opening its content until every outcome without a body is
/// resolved: <see cref="TrySelectContent"/> answers <c>304</c>/<c>412</c>/<c>416</c> itself and
/// returns <see langword="false"/>; only when it returns <see langword="true"/> does the caller open
/// the content and call <see cref="WriteContentAsync"/>. A <c>304</c>/<c>412</c>/<c>416</c> therefore
/// never touches the content.
/// </remarks>
internal static class RepresentationWriter
{
    private const int copyBufferSize = 64 * 1024;

    /// <summary>
    /// Serves <paramref name="file"/>, opening it only when the outcome has content. Its length and
    /// validators are read from the file here (<see cref="RepresentationMetadata.WithFile"/>);
    /// <paramref name="presentation"/> supplies the content type and the fields that ride on every
    /// outcome. A file that no longer exists when its metadata is read or when it is opened answers
    /// <c>404</c>, since nothing has been committed to the response at either point.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="file">The file whose content is served.</param>
    /// <param name="presentation">The content type, and any <c>Content-Encoding</c>, <c>Cache-Control</c>, and <c>Vary</c> to emit.</param>
    /// <param name="cancellationToken">A token that cancels the content copy.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    public static async Task SendFileAsync(
        IHttpContext context,
        IFileSystemFile file,
        RepresentationMetadata presentation,
        CancellationToken cancellationToken)
    {
        RepresentationMetadata representation;
        try
        {
            representation = presentation.WithFile(file);
        }
        catch (Exception exception) when (IsMissingFile(exception))
        {
            // The file vanished after it was resolved (the physical mount reports that as soon as
            // its size is read); nothing has been committed to the response yet.
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        // IFileSystemFile reports -1 for a file that does not exist.
        if (representation.Length is < 0)
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        if (!TrySelectContent(context, representation, out HttpRangeSlice? slice))
        {
            return;
        }

        Stream source;
        try
        {
            // Read access with shared reading: concurrent responses for one file must not lock each
            // other out (the parameterless Open() denies all sharing, and asks for write access on a
            // writable mount). Delete sharing lets a deployment delete or rename the file while it is
            // served; the open handle keeps reading the bytes its validators describe. Writers stay
            // refused, so the content cannot change under the copy.
            source = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception exception) when (IsMissingFile(exception))
        {
            // The file vanished between resolution and open; nothing has been committed to the
            // response yet, so an honest 404 is still available.
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await using (source.ConfigureAwait(false))
        {
            await WriteContentAsync(context, representation, slice, source, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves the remaining bytes of <paramref name="source"/> described by
    /// <paramref name="representation"/>. The stream is read from its current position and is not
    /// disposed.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="representation">The stream's representation metadata.</param>
    /// <param name="source">The stream whose remaining bytes are the content.</param>
    /// <param name="cancellationToken">A token that cancels the content copy.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    public static Task WriteStreamAsync(
        IHttpContext context,
        in RepresentationMetadata representation,
        Stream source,
        CancellationToken cancellationToken)
    {
        return TrySelectContent(context, representation, out HttpRangeSlice? slice)
            ? WriteContentAsync(context, representation, slice, source, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Evaluates the request's preconditions and range against <paramref name="representation"/>. An
    /// outcome without content — <c>304</c>, <c>412</c>, or <c>416</c> — is written here and
    /// <see langword="false"/> is returned; otherwise <see langword="true"/> is returned with the
    /// single byte range to send, or <see langword="null"/> for the full representation.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="representation">The representation the request is evaluated against.</param>
    /// <param name="slice">When this method returns <see langword="true"/>, the range to send, or <see langword="null"/> for the full representation.</param>
    /// <returns><see langword="true"/> when the caller should write the content; <see langword="false"/> when the response is complete.</returns>
    public static bool TrySelectContent(IHttpContext context, in RepresentationMetadata representation, out HttpRangeSlice? slice)
    {
        slice = null;

        IHttpRequest request = context.Request;
        HttpMethod method = request.Method;

        switch (EvaluatePreconditions(request, method, representation.ETag, representation.LastModified))
        {
            case HttpPreconditionOutcome.NotModified:
                WriteNotModified(context.Response, representation);
                return false;
            case HttpPreconditionOutcome.PreconditionFailed:
                context.Response.StatusCode = HttpStatusCode.PreconditionFailed;
                return false;
        }

        // Range applies to GET only (RFC 9110 §14.2 — Range on HEAD has no defined effect;
        // this server ignores it), only to a representation of known length, and only when
        // If-Range, if present, still matches.
        if (method == HttpMethod.Get
            && representation.Length is long length
            && TryGetRangeSelection(request, representation.ETag, representation.LastModified, length, out HttpRangeSelection selection))
        {
            if (selection.Status == HttpRangeSelectionStatus.Unsatisfiable)
            {
                WriteRangeNotSatisfiable(context.Response, selection, representation);
                return false;
            }

            // Only a single satisfiable range produces a 206; a multi-range set deliberately
            // falls back to the full 200 representation (multipart/byteranges is out of scope).
            if (selection.IsSingleSlice)
            {
                slice = selection.Slices[0];
            }
        }

        return true;
    }

    /// <summary>
    /// Writes the <c>200</c> or <c>206</c> header section for <paramref name="representation"/> and,
    /// unless the request is <c>HEAD</c>, its content from <paramref name="source"/>: the bytes of
    /// <paramref name="slice"/>; otherwise exactly the representation's length when it is known, or
    /// every remaining byte when it is not.
    /// </summary>
    /// <param name="context">The exchange.</param>
    /// <param name="representation">The representation being served.</param>
    /// <param name="slice">The byte range selected by <see cref="TrySelectContent"/>, or <see langword="null"/>.</param>
    /// <param name="source">The content, positioned at the representation's first byte.</param>
    /// <param name="cancellationToken">A token that cancels the content copy.</param>
    /// <returns>A task that completes when the content has been written.</returns>
    public static async Task WriteContentAsync(
        IHttpContext context,
        RepresentationMetadata representation,
        HttpRangeSlice? slice,
        Stream source,
        CancellationToken cancellationToken)
    {
        IHttpResponse response = context.Response;
        IHttpHeaderCollection headers = response.Headers;

        response.StatusCode = slice is null ? HttpStatusCode.Ok : HttpStatusCode.PartialContent;
        headers[HttpHeaderKey.ContentType] = representation.ContentType;

        // A representation of unknown length is delimited by the transport (chunked framing on
        // HTTP/1.1, END_STREAM on HTTP/2 and HTTP/3) and cannot serve ranges, so it advertises none.
        if (representation.Length is long length)
        {
            headers[HttpHeaderKey.ContentLength] = (slice?.Length ?? length).ToString(CultureInfo.InvariantCulture);
            headers[HttpHeaderKey.AcceptRanges] = HttpRangeHeader.BytesUnit;
        }

        WriteValidators(headers, representation);
        WriteCacheControl(headers, representation);

        if (representation.ContentEncoding is not null)
        {
            headers[HttpHeaderKey.ContentEncoding] = representation.ContentEncoding;
        }
        if (representation.VaryByAcceptEncoding)
        {
            AppendVaryAcceptEncoding(headers);
        }
        if (slice is HttpRangeSlice partial)
        {
            headers[HttpHeaderKey.ContentRange] = partial.ContentRange.ToString();
        }

        if (context.Request.Method == HttpMethod.Head)
        {
            // RFC 9110 §9.3.2: same header section as GET, never a body. The transports
            // suppress HEAD bodies as well; skipping the copy here saves the read.
            return;
        }

        if (slice is HttpRangeSlice single)
        {
            await CopyExactAsync(source, response.Body, single.Offset, single.Length, cancellationToken).ConfigureAwait(false);
        }
        else if (representation.Length is long declared)
        {
            // The header section promised exactly this many bytes, so copy exactly that many: content
            // that grew since its length was read is cut at the declared length, and content that
            // shrank aborts the response, as a short range does — either way HTTP/1.1 framing holds.
            await CopyExactAsync(source, response.Body, 0, declared, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // No length was declared; the transport delimits the body, so every byte goes.
            await source.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
    }

    // A mount reports a missing file through its own exception type (FileSystemException) when it
    // maps the failure, and through the BCL's when it does not (the physical and in-memory mounts'
    // Open, the physical mount's Size).
    private static bool IsMissingFile(Exception exception)
        => exception is FileSystemException or FileNotFoundException or DirectoryNotFoundException;

    private static HttpPreconditionOutcome EvaluatePreconditions(
        IHttpRequest request,
        HttpMethod method,
        HttpEntityTag? etag,
        DateTimeOffset? lastModified)
    {
        // Malformed precondition fields are treated as absent: HttpEntityTagCondition parses
        // strictly (RFC 9110 §13.1.1/§13.1.2 lists), and §13.1.3/§13.1.4 say an unparseable
        // date is ignored.
        HttpEntityTagCondition? ifMatch = null;
        if (request.Headers.TryGetValue(HttpHeaderKey.IfMatch, out HttpHeaderValue ifMatchValue)
            && HttpEntityTagCondition.TryParse((string?)ifMatchValue, out HttpEntityTagCondition parsedIfMatch))
        {
            ifMatch = parsedIfMatch;
        }

        HttpEntityTagCondition? ifNoneMatch = null;
        if (request.Headers.TryGetValue(HttpHeaderKey.IfNoneMatch, out HttpHeaderValue ifNoneMatchValue)
            && HttpEntityTagCondition.TryParse((string?)ifNoneMatchValue, out HttpEntityTagCondition parsedIfNoneMatch))
        {
            ifNoneMatch = parsedIfNoneMatch;
        }

        DateTimeOffset? ifModifiedSince = null;
        if (request.Headers.TryGetValue(HttpHeaderKey.IfModifiedSince, out HttpHeaderValue ifModifiedSinceValue)
            && HttpDate.TryParse((string?)ifModifiedSinceValue, out DateTimeOffset parsedIfModifiedSince))
        {
            ifModifiedSince = parsedIfModifiedSince;
        }

        DateTimeOffset? ifUnmodifiedSince = null;
        if (request.Headers.TryGetValue(HttpHeaderKey.IfUnmodifiedSince, out HttpHeaderValue ifUnmodifiedSinceValue)
            && HttpDate.TryParse((string?)ifUnmodifiedSinceValue, out DateTimeOffset parsedIfUnmodifiedSince))
        {
            ifUnmodifiedSince = parsedIfUnmodifiedSince;
        }

        return HttpConditionalRequest.Evaluate(new HttpConditionalRequestContext
        {
            Method = method,
            ETag = etag,
            LastModified = lastModified,
            // The representation being served exists even when it has no validators, so the "*"
            // forms of If-Match and If-None-Match match it (RFC 9110 §13.1.1, §13.1.2).
            HasCurrentRepresentation = true,
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch,
            IfModifiedSince = ifModifiedSince,
            IfUnmodifiedSince = ifUnmodifiedSince,
        });
    }

    private static bool TryGetRangeSelection(
        IHttpRequest request,
        HttpEntityTag? etag,
        DateTimeOffset? lastModified,
        long length,
        out HttpRangeSelection selection)
    {
        selection = default;

        if (!request.Headers.TryGetValue(HttpHeaderKey.Range, out HttpHeaderValue rangeValue))
        {
            return false;
        }

        // RFC 9110 §13.2.2 step 5: a present If-Range gates whether the Range is honored at
        // all. An unparseable If-Range cannot be validated, so the range is ignored; so is one
        // whose validator the representation does not have.
        if (request.Headers.TryGetValue(HttpHeaderKey.IfRange, out HttpHeaderValue ifRangeValue))
        {
            if (!HttpIfRange.TryParse((string?)ifRangeValue, out HttpIfRange ifRange)
                || !ifRange.Matches(etag, lastModified))
            {
                return false;
            }
        }

        // An unrecognized unit or malformed range-set fails the parse — the RFC's signal to
        // ignore the header and serve the full representation.
        if (!HttpRangeHeader.TryParse((string?)rangeValue, out HttpRangeHeader range))
        {
            return false;
        }

        selection = HttpRangeSelector.Select(range, length);
        return true;
    }

    private static void WriteNotModified(IHttpResponse response, in RepresentationMetadata representation)
    {
        // RFC 9110 §15.4.5: a 304 carries the headers a 200 would need for cache updating —
        // validators, Cache-Control, Vary — and no content headers or body.
        IHttpHeaderCollection headers = response.Headers;

        response.StatusCode = HttpStatusCode.NotModified;
        WriteValidators(headers, representation);
        WriteCacheControl(headers, representation);

        if (representation.VaryByAcceptEncoding)
        {
            AppendVaryAcceptEncoding(headers);
        }
    }

    private static void WriteRangeNotSatisfiable(
        IHttpResponse response,
        in HttpRangeSelection selection,
        in RepresentationMetadata representation)
    {
        IHttpHeaderCollection headers = response.Headers;

        response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
        // RFC 9110 §14.4: the unsatisfied-range form reports the selected representation's
        // complete length so the client can retry with a valid range.
        headers[HttpHeaderKey.ContentRange] = selection.UnsatisfiedContentRange.ToString();
        headers[HttpHeaderKey.AcceptRanges] = HttpRangeHeader.BytesUnit;
        WriteValidators(headers, representation);
        WriteCacheControl(headers, representation);

        if (representation.VaryByAcceptEncoding)
        {
            AppendVaryAcceptEncoding(headers);
        }
    }

    private static void WriteValidators(IHttpHeaderCollection headers, in RepresentationMetadata representation)
    {
        if (representation.ETag is HttpEntityTag etag)
        {
            headers[HttpHeaderKey.ETag] = etag.ToString();
        }
        if (representation.LastModified is DateTimeOffset lastModified)
        {
            headers[HttpHeaderKey.LastModified] = HttpDate.Format(lastModified);
        }
    }

    private static void WriteCacheControl(IHttpHeaderCollection headers, in RepresentationMetadata representation)
    {
        if (representation.CacheControl is not null)
        {
            headers[HttpHeaderKey.CacheControl] = representation.CacheControl;
        }
    }

    private static void AppendVaryAcceptEncoding(IHttpHeaderCollection headers)
    {
        if (!headers.TryGetValue(HttpHeaderKey.Vary, out HttpHeaderValue existing))
        {
            headers[HttpHeaderKey.Vary] = "Accept-Encoding";
            return;
        }

        string current = (string?)existing ?? string.Empty;
        ReadOnlySpan<char> span = current.AsSpan();
        foreach (Range segment in span.Split(','))
        {
            ReadOnlySpan<char> token = span[segment].Trim();
            if (token.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) || token is "*")
            {
                return;
            }
        }

        headers[HttpHeaderKey.Vary] = current.Length == 0 ? "Accept-Encoding" : current + ", Accept-Encoding";
    }

    private static async Task CopyExactAsync(Stream source, Stream destination, long offset, long count, CancellationToken cancellationToken)
    {
        // Offsets are relative to the representation's first byte, which is the stream's current
        // position: a freshly opened file stream sits at 0, and a handler's stream is served from
        // wherever the handler left it.
        if (offset > 0)
        {
            if (source.CanSeek)
            {
                source.Seek(offset, SeekOrigin.Current);
            }
            else
            {
                await SkipAsync(source, offset, cancellationToken).ConfigureAwait(false);
            }
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(copyBufferSize);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int read = await source
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    // The content shrank after the head (with its Content-Length and any
                    // Content-Range) was computed: completing the response would silently serve
                    // wrong bytes, so abort it.
                    throw new EndOfStreamException("The content ended before its declared length was fully written.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task SkipAsync(Stream source, long count, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(copyBufferSize);
        try
        {
            long remaining = count;
            while (remaining > 0)
            {
                int read = await source
                    .ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new EndOfStreamException("The content ended before the selected byte range was reached.");
                }
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
