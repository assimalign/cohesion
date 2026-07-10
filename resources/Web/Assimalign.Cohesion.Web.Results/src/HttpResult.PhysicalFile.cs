using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A file result over a physical file path: opens the file, infers the <c>Content-Type</c> from
/// the file name via <see cref="HttpContentTypes.GetContentType"/> when none was supplied, and
/// copies the contents to the response body with <c>Content-Length</c>.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.PhysicalFile"/> or <see cref="TypedResults.PhysicalFile"/>;
/// the constructor is internal so the factories remain the only entry point. This is the
/// <em>unconditional</em> variant: range requests and preconditions are deferred to the
/// precondition-aware file result (#777). I/O errors surface as-is — a missing file throws
/// <see cref="FileNotFoundException"/> rather than being translated to a status code; mapping file
/// system state to 404s is a static-file-middleware concern (#777), not a result concern. The
/// carrier is immutable and may be reused across exchanges.
/// </remarks>
public sealed class PhysicalFileHttpResult : IResult
{
    internal PhysicalFileHttpResult(string path, string? contentType)
    {
        Path = path;
        ContentType = contentType ?? HttpContentTypes.GetContentType(path);
    }

    /// <summary>
    /// Gets the absolute or working-directory-relative path of the file to send.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets. When the factory was given none, it is
    /// inferred from the file extension via <see cref="HttpContentTypes.GetContentType"/>
    /// (falling back to <c>application/octet-stream</c>).
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Opens the file at <see cref="Path"/> and copies it to the response body with
    /// <see cref="ContentType"/> and <c>Content-Length</c>. The status code is left untouched.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the copy.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">The file at <see cref="Path"/> does not exist.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    public async Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        FileStream source = new(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using (source.ConfigureAwait(false))
        {
            IHttpResponse response = context.Response;

            response.Headers[HttpHeaderKey.ContentType] = ContentType;
            response.Headers[HttpHeaderKey.ContentLength] = source.Length.ToString(CultureInfo.InvariantCulture);

            await source.CopyToAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }
    }
}
