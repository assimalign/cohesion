using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.StaticFiles.Internal;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Response helpers that send a file or a stream from a handler with the protocol behavior
/// <c>UseStaticFiles</c> gives an asset: a <c>Content-Type</c>, <c>ETag</c> and <c>Last-Modified</c>
/// validators, conditional requests (RFC 9110 &#167; 13), and single byte ranges (RFC 9110 &#167; 14).
/// </summary>
/// <remarks>
/// <para>
/// Each helper reads the request through <see cref="IHttpResponse.HttpContext"/> — its method and its
/// <c>If-Match</c>, <c>If-None-Match</c>, <c>If-Modified-Since</c>, <c>If-Unmodified-Since</c>,
/// <c>Range</c>, and <c>If-Range</c> fields — and answers with one of these outcomes:
/// </para>
/// <list type="bullet">
///   <item><description><c>200 OK</c> with the full representation.</description></item>
///   <item><description><c>206 Partial Content</c> with one byte range and its <c>Content-Range</c>, for a <c>GET</c> whose <c>Range</c> names a single satisfiable range and whose <c>If-Range</c>, if present, still matches.</description></item>
///   <item><description><c>304 Not Modified</c> with the validators and no content, when <c>If-None-Match</c> or <c>If-Modified-Since</c> shows that the client's copy of a <c>GET</c> or <c>HEAD</c> is current.</description></item>
///   <item><description><c>412 Precondition Failed</c> with no content, when <c>If-Match</c> or <c>If-Unmodified-Since</c> fails.</description></item>
///   <item><description><c>416 Range Not Satisfiable</c> with <c>Content-Range: bytes */N</c>, when no requested range overlaps the representation.</description></item>
/// </list>
/// <para>
/// A <c>HEAD</c> request gets the <c>GET</c> header section and no content. A request for several
/// ranges gets the full representation (<c>multipart/byteranges</c> is not produced), and a
/// <c>Range</c> on any method but <c>GET</c> is ignored. Preconditions and ranges are evaluated by the
/// shared <see cref="HttpConditionalRequest"/>, <see cref="HttpRangeSelector"/>, and
/// <see cref="HttpIfRange"/> primitives, in the same engine the <c>UseStaticFiles</c> middleware runs.
/// </para>
/// <para>
/// The helpers set the status code and own the <c>Content-Type</c>, <c>Content-Length</c>,
/// <c>Content-Range</c>, <c>Accept-Ranges</c>, <c>ETag</c>, and <c>Last-Modified</c> fields. Fields
/// the application set before the call, such as <c>Cache-Control</c> or <c>Content-Disposition</c>,
/// are left in place, so they also ride on a <c>304</c>.
/// </para>
/// </remarks>
public static class HttpResponseFileExtensions
{
    extension(IHttpResponse response)
    {
        /// <summary>
        /// Sends <paramref name="file"/> as the response, with validators derived from the file and
        /// support for conditional and single-range requests.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <c>ETag</c> is a strong entity-tag derived from the file's <c>Size</c> and
        /// <c>UpdatedOn</c>, and <c>Last-Modified</c> is <c>UpdatedOn</c>: the validators
        /// <c>UseStaticFiles</c> emits for the same file. The file is opened only when the outcome has
        /// content, so a <c>304</c>, <c>412</c>, or <c>416</c> never reads it, and a file that no
        /// longer exists by the time it is read is answered with <c>404 Not Found</c>. It is opened
        /// for shared reading, so any number of responses can send it at once, and it may be deleted
        /// or renamed while a response is sending it.
        /// </para>
        /// <para>
        /// When <paramref name="contentType"/> is <see langword="null"/>, the type is looked up from the
        /// file name's extension in <see cref="HttpContentTypes.Default"/>. A name whose extension is
        /// unmapped, or that has none (<c>html</c>, or a dotfile such as <c>.json</c>), is sent as
        /// <c>application/octet-stream</c>. Pass the type explicitly for content a user supplied: a
        /// file named <c>avatar.html</c> would otherwise be served as <c>text/html</c>.
        /// </para>
        /// </remarks>
        /// <param name="file">The file to send.</param>
        /// <param name="contentType">The <c>Content-Type</c> to send, or <see langword="null"/> to derive it from the file name.</param>
        /// <param name="cancellationToken">A token that cancels the content copy. When it cannot be cancelled, the exchange's <see cref="IHttpContext.RequestCancelled"/> token is observed instead.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="file"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="contentType"/> is not a concrete media type (RFC 9110 &#167; 8.3.1) or contains a control character.</exception>
        public Task SendFileAsync(IFileSystemFile file, string? contentType = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(file);

            string resolvedContentType = contentType is null
                ? HttpContentTypes.GetFromFileName(file.Name)
                : ValidateContentType(contentType);

            return SendFileCoreAsync(response, file, resolvedContentType, cancellationToken);
        }

        /// <summary>
        /// Sends the file at <paramref name="path"/> in <paramref name="fileSystem"/> as the response,
        /// or answers <c>404 Not Found</c> when the mount holds no such file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="path"/> is resolved inside <paramref name="fileSystem"/> and can never address
        /// anything outside it, so it may be built from request input such as a route value. A path that
        /// contains a <c>.</c> or <c>..</c> segment (separated by <c>/</c> or <c>\</c>), a <c>:</c> (drive
        /// letters and alternate data streams), or a NUL is answered with <c>404</c> without consulting
        /// the mount, and a leading <c>/</c> refers to the mount root rather than the host's: the checks
        /// <c>UseStaticFiles</c> applies to request paths. A path that names a directory, or nothing, is
        /// also answered with <c>404</c>.
        /// </para>
        /// <para>
        /// A resolved file is sent exactly as the <see cref="IFileSystemFile"/> overload sends it. The
        /// mount is the security boundary: to serve the files of one directory, mount that directory
        /// (a read-only <see cref="PhysicalFileSystem"/> rooted there, for example) rather than a parent
        /// whose other contents must stay private.
        /// </para>
        /// </remarks>
        /// <param name="fileSystem">The mount <paramref name="path"/> is resolved in.</param>
        /// <param name="path">The file's path relative to the root of <paramref name="fileSystem"/>, for example <c>reports/2026.pdf</c>.</param>
        /// <param name="contentType">The <c>Content-Type</c> to send, or <see langword="null"/> to derive it from the file name.</param>
        /// <param name="cancellationToken">A token that cancels the content copy. When it cannot be cancelled, the exchange's <see cref="IHttpContext.RequestCancelled"/> token is observed instead.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/>, <paramref name="fileSystem"/>, or <paramref name="path"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="contentType"/> is not a concrete media type (RFC 9110 &#167; 8.3.1) or contains a control character.</exception>
        public Task SendFileAsync(IFileSystem fileSystem, string path, string? contentType = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(fileSystem);
            ArgumentNullException.ThrowIfNull(path);

            // Validated before the lookup, so a bad argument fails the same way whether or not the
            // file exists.
            string? explicitContentType = contentType is null ? null : ValidateContentType(contentType);

            if (!StaticFilePath.TryResolveFile(fileSystem, path, out IFileSystemFile? file))
            {
                response.StatusCode = HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            return SendFileCoreAsync(
                response,
                file,
                explicitContentType ?? HttpContentTypes.GetFromFileName(file.Name),
                cancellationToken);
        }

        /// <summary>
        /// Writes the remaining bytes of <paramref name="stream"/> as the response, with
        /// caller-supplied validators and support for conditional requests and, for a seekable
        /// stream, single-range requests.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The representation is the stream's remaining bytes, from its current position to its end.
        /// A seekable stream therefore has a known length: it is sent with <c>Content-Length</c> and
        /// <c>Accept-Ranges: bytes</c>, and a single byte range is served by seeking to it. A stream
        /// that cannot seek is sent whole, without <c>Content-Length</c>, in a body the transport
        /// delimits; a <c>Range</c> request for it gets the full representation, which RFC 9110
        /// &#167; 14.2 permits. A <c>HEAD</c> request never reads the stream.
        /// </para>
        /// <para>
        /// Validators are never computed from the content: a stream can be large, one-shot, or costly
        /// to read, and hashing it would turn every request, a <c>304</c> included, into a full read.
        /// Supply <paramref name="entityTag"/> and <paramref name="lastModified"/> when the content
        /// has them (a blob's version, a row's update time), and conditional and <c>If-Range</c>
        /// requests are evaluated against them. Without them, only the <c>*</c> forms of
        /// <c>If-Match</c> and <c>If-None-Match</c> can match, and a request carrying <c>If-Range</c>
        /// gets the full representation. A weak <paramref name="entityTag"/> can satisfy
        /// <c>If-None-Match</c>, but never <c>If-Match</c> or <c>If-Range</c>, which compare strongly.
        /// </para>
        /// <para>
        /// The stream is not disposed; whoever opened it disposes it once the returned task completes.
        /// </para>
        /// </remarks>
        /// <param name="stream">The readable stream whose remaining bytes are sent.</param>
        /// <param name="contentType">The <c>Content-Type</c> to send, or <see langword="null"/> for <c>application/octet-stream</c>.</param>
        /// <param name="entityTag">The representation's entity-tag, or <see langword="null"/> when it has none.</param>
        /// <param name="lastModified">The representation's last-modification time, or <see langword="null"/> when unknown. It is sent and compared at whole-second (HTTP-date) precision.</param>
        /// <param name="cancellationToken">A token that cancels the content copy. When it cannot be cancelled, the exchange's <see cref="IHttpContext.RequestCancelled"/> token is observed instead.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="response"/> or <paramref name="stream"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="stream"/> is not readable, <paramref name="entityTag"/> is an empty (default) entity-tag, or
        /// <paramref name="contentType"/> is not a concrete media type (RFC 9110 &#167; 8.3.1) or contains a control character.
        /// </exception>
        public Task WriteStreamAsync(
            Stream stream,
            string? contentType = null,
            HttpEntityTag? entityTag = null,
            DateTimeOffset? lastModified = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(stream);

            if (!stream.CanRead)
            {
                throw new ArgumentException("The stream must be readable.", nameof(stream));
            }
            if (entityTag is { IsEmpty: true })
            {
                throw new ArgumentException("The entity-tag must not be empty.", nameof(entityTag));
            }

            RepresentationMetadata representation = new()
            {
                ContentType = contentType is null ? HttpContentTypes.Fallback : ValidateContentType(contentType),
                // Only a seekable stream knows how many bytes remain, and only it can reach a range
                // without reading (and discarding) everything before it.
                Length = stream.CanSeek ? Math.Max(0L, stream.Length - stream.Position) : null,
                ETag = entityTag,
                LastModified = lastModified is DateTimeOffset modified
                    ? RepresentationMetadata.TruncateToSeconds(modified)
                    : null,
            };

            IHttpContext context = response.HttpContext;

            return RepresentationWriter.WriteStreamAsync(
                context,
                representation,
                stream,
                ResolveCancellation(context, cancellationToken));
        }
    }

    private static Task SendFileCoreAsync(
        IHttpResponse response,
        IFileSystemFile file,
        string contentType,
        CancellationToken cancellationToken)
    {
        IHttpContext context = response.HttpContext;

        return RepresentationWriter.SendFileAsync(
            context,
            file,
            new RepresentationMetadata { ContentType = contentType },
            ResolveCancellation(context, cancellationToken));
    }

    // A caller that passes no token still stops copying when the client goes away.
    private static CancellationToken ResolveCancellation(IHttpContext context, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled ? cancellationToken : context.RequestCancelled;

    private static string ValidateContentType(string contentType)
    {
        // The value is written into the header section verbatim, and HttpMediaType skips a malformed
        // parameter rather than failing the parse, so control characters (CR and LF above all) are
        // refused explicitly. A media range such as "text/*" is not a content type.
        if (contentType.AsSpan().ContainsAnyInRange('\0', '\x1F')
            || contentType.Contains('\x7F')
            || !HttpMediaType.TryParse(contentType, out HttpMediaType mediaType)
            || mediaType.HasWildcard)
        {
            throw new ArgumentException(
                $"The content type must be a concrete media type, such as 'application/pdf': '{contentType}'.",
                nameof(contentType));
        }

        return contentType;
    }
}
