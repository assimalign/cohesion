using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Serves <c>GET</c>/<c>HEAD</c> requests under a request-path prefix from a mounted
/// <see cref="IFileSystem"/>, composing the shared protocol primitives: RFC 9110 &#167; 13
/// preconditions via <see cref="HttpConditionalRequest"/>, &#167; 14 byte ranges via
/// <see cref="HttpRangeSelector"/>, content types via <see cref="HttpContentTypes"/>, and
/// <c>Accept-Encoding</c> negotiation of precompressed siblings via
/// <see cref="HttpContentNegotiation"/>. All composition state is frozen at construction —
/// no request-time service location, no per-request allocation beyond the exchange itself.
/// </summary>
internal sealed class StaticFilesMiddleware : IWebApplicationMiddleware
{
    private const int CopyBufferSize = 64 * 1024;

    // Precompressed sibling codings in server preference order (RFC 9110 §12.5.3 lets the
    // server break client ties): brotli first, then gzip.
    private static readonly (string Coding, string Suffix)[] PrecompressedCodings =
    [
        ("br", ".br"),
        ("gzip", ".gz"),
    ];

    private readonly IFileSystem _fileSystem;
    private readonly string _prefix;
    private readonly string[] _defaultDocuments;
    private readonly string? _cacheControl;
    private readonly FrozenDictionary<string, string> _contentTypes;
    private readonly bool _serveUnknownContentTypes;
    private readonly string _fallbackContentType;
    private readonly bool _servePrecompressedAssets;

    public StaticFilesMiddleware(IFileSystem fileSystem, StaticFilesOptions options)
    {
        _fileSystem = fileSystem;
        // "/" mounts at the site root (empty prefix); anything else is stored without a
        // trailing slash so segment-aligned matching stays uniform.
        _prefix = options.RequestPath.Value == "/" ? string.Empty : options.RequestPath.Value.TrimEnd('/');
        _defaultDocuments = [.. options.DefaultDocuments];
        _cacheControl = string.IsNullOrEmpty(options.CacheControl) ? null : options.CacheControl;
        _contentTypes = options.ContentTypeMappings.Count == 0
            ? HttpContentTypes.Default
            : HttpContentTypes.CreateMap(options.ContentTypeMappings);
        _serveUnknownContentTypes = options.ServeUnknownContentTypes;
        _fallbackContentType = options.FallbackContentType;
        _servePrecompressedAssets = options.ServePrecompressedAssets;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        HttpMethod method = context.Request.Method;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            await next.Invoke(context);
            return;
        }

        // Transport-parity compensation: the h2/h3 transports percent-decode the request path
        // before it reaches middleware (HttpPath.FromUriComponent), but HTTP/1.x currently
        // surfaces the raw request-target text. Decode here so the traversal gate and the file
        // lookup see the same text on every transport — otherwise "%2e%2e" would read as a
        // literal segment name on h1 and as ".." on h2. Remove once the h1 transport gains
        // decode parity (filed as a follow-up; see docs/DESIGN.md).
        string path = context.Request.Path.Value;
        if (context.Version == HttpVersion.Http11 && path.Contains('%'))
        {
            path = Uri.UnescapeDataString(path);
        }

        if (!StaticFilePath.TryGetRelativePath(path, _prefix, out string remainder))
        {
            await next.Invoke(context);
            return;
        }

        // The traversal gate: under our prefix, a path with dot segments (or NUL/':') is
        // hostile or nonsensical — answer 404 directly rather than letting it reach any
        // resolver. See StaticFilePath.HasUnsafeSegments for what the transports decode.
        if (StaticFilePath.HasUnsafeSegments(remainder))
        {
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        // An exact-prefix match ("" remainder) is the mount root addressed without a trailing
        // slash; normalize the lookup to the file-system root and remember the slash for the
        // default-document redirect decision.
        bool hadTrailingSlash = remainder.Length > 0 && remainder[^1] == '/';
        string candidate = remainder.Length == 0 ? "/" : remainder;

        FileSystemPath filePath;
        try
        {
            filePath = FileSystemPath.Parse(candidate);
        }
        catch (ArgumentException)
        {
            // Second defense layer: FileSystemPath rejects interior dot segments and illegal
            // path characters outright. Anything it refuses is not a servable file.
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        // The mount root exists by definition; everything else must resolve through the mount.
        IFileSystemInfo? info = null;
        if (candidate != "/" && !TryGetInfo(filePath, out info))
        {
            await next.Invoke(context);
            return;
        }

        string logicalName;
        IFileSystemFile file;

        // Directory detection is by info type, not FileAttributes: not every mount stamps
        // FileAttributes.Directory (InMemory leaves attributes unset), but every mount returns
        // an IFileSystemDirectory-typed info for a directory.
        if (info is null || info is IFileSystemDirectory)
        {
            if (!TryResolveDefaultDocument(candidate, out IFileSystemFile? document, out string documentName))
            {
                // No default document (or none configured): directory browsing is a deferred
                // follow-up, so the directory itself is not servable.
                await next.Invoke(context);
                return;
            }

            if (!hadTrailingSlash)
            {
                // Serving directory content at the slash-less URL would break every relative
                // link inside the document, so canonicalize first (RFC 9110 §15.4.2).
                RedirectAppendingSlash(context);
                return;
            }

            file = document;
            logicalName = documentName;
        }
        else
        {
            if (info is not IFileSystemFile resolved)
            {
                await next.Invoke(context);
                return;
            }

            file = resolved;
            logicalName = GetFileName(candidate);
        }

        // Content-type gate before any validator work: a file this middleware will not claim
        // should never emit validators or 304s. The logical (unencoded) name decides the type —
        // a precompressed sibling only changes the coding, never the media type.
        if (!HttpContentTypes.TryGetContentType(_contentTypes, logicalName, out string contentType))
        {
            if (!_serveUnknownContentTypes)
            {
                await next.Invoke(context);
                return;
            }
            contentType = _fallbackContentType;
        }

        // Negotiate the representation (identity vs precompressed sibling) before evaluating
        // preconditions: validators belong to the representation actually selected.
        IFileSystemFile servedFile = file;
        string? contentEncoding = null;
        bool varyByAcceptEncoding = false;

        if (_servePrecompressedAssets)
        {
            SelectPrecompressedSibling(context, candidate, ref servedFile, ref contentEncoding, ref varyByAcceptEncoding);
        }

        long length = servedFile.Size;
        DateTimeOffset lastModified = TruncateToSeconds(NormalizeTimestamp(servedFile.UpdatedOn));
        // Strong validator from the served representation's Size + UpdatedOn: hex ticks and hex
        // length are valid etagc characters, and a sibling naturally yields a different tag than
        // the identity file — distinct representations must not share a strong ETag.
        HttpEntityTag etag = HttpEntityTag.Strong(string.Create(
            CultureInfo.InvariantCulture,
            $"{NormalizeTimestamp(servedFile.UpdatedOn).UtcTicks:x}-{length:x}"));

        switch (EvaluatePreconditions(context.Request, method, etag, lastModified))
        {
            case HttpPreconditionOutcome.NotModified:
                WriteNotModified(context, etag, lastModified, varyByAcceptEncoding);
                return;
            case HttpPreconditionOutcome.PreconditionFailed:
                context.Response.StatusCode = HttpStatusCode.PreconditionFailed;
                return;
        }

        // Range applies to GET only (RFC 9110 §14.2 — Range on HEAD has no defined effect;
        // this server ignores it) and only when If-Range, if present, still matches.
        HttpRangeSlice? slice = null;
        if (method == HttpMethod.Get
            && TryGetRangeSelection(context.Request, etag, lastModified, length, out HttpRangeSelection selection))
        {
            if (selection.Status == HttpRangeSelectionStatus.Unsatisfiable)
            {
                WriteRangeNotSatisfiable(context, selection, etag, lastModified, varyByAcceptEncoding);
                return;
            }

            // Only a single satisfiable range produces a 206; a multi-range set deliberately
            // falls back to the full 200 representation (multipart/byteranges is out of scope).
            if (selection.IsSingleSlice)
            {
                slice = selection.Slices[0];
            }
        }

        Stream source;
        try
        {
            source = servedFile.Open();
        }
        catch (FileSystemException)
        {
            // The file vanished between resolution and open; nothing has been committed to the
            // response yet, so an honest 404 is still available.
            context.Response.StatusCode = HttpStatusCode.NotFound;
            return;
        }

        await using (source.ConfigureAwait(false))
        {
            IHttpHeaderCollection headers = context.Response.Headers;

            context.Response.StatusCode = slice is null ? HttpStatusCode.Ok : HttpStatusCode.PartialContent;
            headers[HttpHeaderKey.ContentType] = contentType;
            headers[HttpHeaderKey.ContentLength] = (slice?.Length ?? length).ToString(CultureInfo.InvariantCulture);
            headers[HttpHeaderKey.AcceptRanges] = HttpRangeHeader.BytesUnit;
            headers[HttpHeaderKey.ETag] = etag.ToString();
            headers[HttpHeaderKey.LastModified] = HttpDate.Format(lastModified);

            if (_cacheControl is not null)
            {
                headers[HttpHeaderKey.CacheControl] = _cacheControl;
            }
            if (contentEncoding is not null)
            {
                headers[HttpHeaderKey.ContentEncoding] = contentEncoding;
            }
            if (varyByAcceptEncoding)
            {
                AppendVaryAcceptEncoding(headers);
            }
            if (slice is HttpRangeSlice partial)
            {
                headers[HttpHeaderKey.ContentRange] = partial.ContentRange.ToString();
            }

            if (method == HttpMethod.Head)
            {
                // RFC 9110 §9.3.2: same header section as GET, never a body. The transports
                // suppress HEAD bodies as well; skipping the copy here saves the file read.
                return;
            }

            if (slice is HttpRangeSlice single)
            {
                await CopySliceAsync(source, context.Response.Body, single.Offset, single.Length, context.RequestCancelled).ConfigureAwait(false);
            }
            else
            {
                await source.CopyToAsync(context.Response.Body, context.RequestCancelled).ConfigureAwait(false);
            }
        }
    }

    private bool TryGetInfo(FileSystemPath path, [NotNullWhen(true)] out IFileSystemInfo? info)
    {
        info = null;
        try
        {
            if (!_fileSystem.Exists(path))
            {
                return false;
            }
            info = _fileSystem.GetInfo(path);
            return true;
        }
        catch (FileSystemException)
        {
            // A lookup race (deleted between Exists and GetInfo) or a mount-specific refusal:
            // either way the path is not servable.
            return false;
        }
    }

    private bool TryResolveDefaultDocument(string directory, [NotNullWhen(true)] out IFileSystemFile? document, out string documentName)
    {
        document = null;
        documentName = string.Empty;

        if (_defaultDocuments.Length == 0)
        {
            return false;
        }

        string directoryPrefix = directory == "/" ? "/" : directory.TrimEnd('/') + "/";
        foreach (string name in _defaultDocuments)
        {
            if (TryGetInfo(FileSystemPath.Parse(directoryPrefix + name), out IFileSystemInfo? info)
                && info is IFileSystemFile candidate)
            {
                document = candidate;
                documentName = name;
                return true;
            }
        }

        return false;
    }

    private void SelectPrecompressedSibling(
        IHttpContext context,
        string candidate,
        ref IFileSystemFile servedFile,
        ref string? contentEncoding,
        ref bool varyByAcceptEncoding)
    {
        Span<int> available = stackalloc int[PrecompressedCodings.Length];
        int count = 0;
        for (int i = 0; i < PrecompressedCodings.Length; i++)
        {
            if (TryGetInfo(FileSystemPath.Parse(candidate + PrecompressedCodings[i].Suffix), out IFileSystemInfo? info)
                && info is IFileSystemFile)
            {
                available[count++] = i;
            }
        }

        if (count == 0)
        {
            return;
        }

        // The same URL can now yield different representations by Accept-Encoding, so every
        // response for it must carry Vary — including the identity one a non-accepting client gets.
        varyByAcceptEncoding = true;

        string[] serverCodings = new string[count];
        for (int i = 0; i < count; i++)
        {
            serverCodings[i] = PrecompressedCodings[available[i]].Coding;
        }

        string? acceptEncoding = context.Request.Headers.TryGetValue(HttpHeaderKey.AcceptEncoding, out HttpHeaderValue acceptEncodingValue)
            ? (string?)acceptEncodingValue
            : null;

        // A false return means even identity was refused (identity;q=0). RFC 9110 §12.5.3 lets
        // the server answer 406 or ignore the field; a static asset server serves identity —
        // refusing cacheable bytes over a hostile-or-misconfigured header helps no one.
        if (!HttpContentNegotiation.TrySelectEncoding(acceptEncoding, serverCodings, out string selected)
            || selected == "identity")
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            (string coding, string suffix) = PrecompressedCodings[available[i]];
            if (coding == selected
                && TryGetInfo(FileSystemPath.Parse(candidate + suffix), out IFileSystemInfo? sibling)
                && sibling is IFileSystemFile siblingFile)
            {
                servedFile = siblingFile;
                contentEncoding = coding;
                return;
            }
        }
    }

    private static HttpPreconditionOutcome EvaluatePreconditions(
        IHttpRequest request,
        HttpMethod method,
        HttpEntityTag etag,
        DateTimeOffset lastModified)
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
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch,
            IfModifiedSince = ifModifiedSince,
            IfUnmodifiedSince = ifUnmodifiedSince,
        });
    }

    private static bool TryGetRangeSelection(
        IHttpRequest request,
        HttpEntityTag etag,
        DateTimeOffset lastModified,
        long length,
        out HttpRangeSelection selection)
    {
        selection = default;

        if (!request.Headers.TryGetValue(HttpHeaderKey.Range, out HttpHeaderValue rangeValue))
        {
            return false;
        }

        // RFC 9110 §13.2.2 step 5: a present If-Range gates whether the Range is honored at
        // all. An unparseable If-Range cannot be validated, so the range is ignored.
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

    private void WriteNotModified(IHttpContext context, HttpEntityTag etag, DateTimeOffset lastModified, bool varyByAcceptEncoding)
    {
        // RFC 9110 §15.4.5: a 304 carries the headers a 200 would need for cache updating —
        // validators, Cache-Control, Vary — and no content headers or body.
        IHttpHeaderCollection headers = context.Response.Headers;

        context.Response.StatusCode = HttpStatusCode.NotModified;
        headers[HttpHeaderKey.ETag] = etag.ToString();
        headers[HttpHeaderKey.LastModified] = HttpDate.Format(lastModified);

        if (_cacheControl is not null)
        {
            headers[HttpHeaderKey.CacheControl] = _cacheControl;
        }
        if (varyByAcceptEncoding)
        {
            AppendVaryAcceptEncoding(headers);
        }
    }

    private void WriteRangeNotSatisfiable(
        IHttpContext context,
        in HttpRangeSelection selection,
        HttpEntityTag etag,
        DateTimeOffset lastModified,
        bool varyByAcceptEncoding)
    {
        IHttpHeaderCollection headers = context.Response.Headers;

        context.Response.StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable;
        // RFC 9110 §14.4: the unsatisfied-range form reports the selected representation's
        // complete length so the client can retry with a valid range.
        headers[HttpHeaderKey.ContentRange] = selection.UnsatisfiedContentRange.ToString();
        headers[HttpHeaderKey.AcceptRanges] = HttpRangeHeader.BytesUnit;
        headers[HttpHeaderKey.ETag] = etag.ToString();
        headers[HttpHeaderKey.LastModified] = HttpDate.Format(lastModified);

        if (_cacheControl is not null)
        {
            headers[HttpHeaderKey.CacheControl] = _cacheControl;
        }
        if (varyByAcceptEncoding)
        {
            AppendVaryAcceptEncoding(headers);
        }
    }

    private static void RedirectAppendingSlash(IHttpContext context)
    {
        // Reconstructing the query from the parsed collection re-encodes each part; a redirect
        // target needs semantic, not byte-for-byte, fidelity.
        string location = context.Request.Path.Value + "/";
        if (context.Request.Query.Count > 0)
        {
            var builder = new StringBuilder(location).Append('?');
            bool first = true;
            foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> pair in context.Request.Query)
            {
                if (!first)
                {
                    builder.Append('&');
                }
                first = false;
                builder.Append(Uri.EscapeDataString(pair.Key.ToString())).Append('=').Append(Uri.EscapeDataString(pair.Value.ToString()));
            }
            location = builder.ToString();
        }

        context.Response.StatusCode = HttpStatusCode.MovedPermanently;
        context.Response.Headers[HttpHeaderKey.Location] = location;
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

    private static string GetFileName(string candidate)
    {
        int separator = candidate.LastIndexOf('/');
        return separator < 0 ? candidate : candidate[(separator + 1)..];
    }

    private static DateTimeOffset NormalizeTimestamp(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
        // File systems that don't stamp a kind report wall-clock UTC in this repo's mounts.
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value)
        // HTTP-date resolution is one second; validators must compare at the emitted precision.
        => new(value.UtcTicks - (value.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static async Task CopySliceAsync(Stream source, Stream destination, long offset, long count, CancellationToken cancellationToken)
    {
        if (source.CanSeek)
        {
            source.Seek(offset, SeekOrigin.Begin);
        }
        else
        {
            await SkipAsync(source, offset, cancellationToken).ConfigureAwait(false);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
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
                    // The file shrank after the head (with its Content-Range) was computed:
                    // completing the response would silently serve wrong bytes, so abort it.
                    throw new EndOfStreamException("The file ended before the selected byte range was fully written.");
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
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
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
                    throw new EndOfStreamException("The file ended before the selected byte range was reached.");
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
