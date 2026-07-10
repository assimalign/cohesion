using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// A buffered file result: writes an in-memory byte payload with an explicit <c>Content-Type</c>
/// and <c>Content-Length</c>.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.File(byte[], string?)"/> or
/// <see cref="TypedResults.File(byte[], string?)"/>; the constructor is internal so the factories
/// remain the only entry point. This is the <em>unconditional</em> variant: range requests and
/// preconditions (<c>Range</c>/<c>If-Range</c>/<c>If-None-Match</c>) are deferred to the
/// precondition-aware file result (#777). The carrier is immutable and may be reused across
/// exchanges.
/// </remarks>
public sealed class FileHttpResult : IResult
{
    internal FileHttpResult(byte[] contents, string? contentType)
    {
        Contents = contents;
        ContentType = contentType ?? HttpContentTypes.Fallback;
    }

    /// <summary>
    /// Gets the file bytes written as the response body.
    /// </summary>
    public ReadOnlyMemory<byte> Contents { get; }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets. Defaults to
    /// <see cref="HttpContentTypes.Fallback"/> (<c>application/octet-stream</c>) when the factory
    /// was given none.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Writes <see cref="Contents"/> to the response body with <see cref="ContentType"/> and
    /// <c>Content-Length</c>. The status code is left untouched.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the body write.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return HttpResultWriter.WritePayloadAsync(context, statusCode: null, ContentType, Contents, cancellationToken);
    }
}
