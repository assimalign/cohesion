using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// A buffered text result: writes a UTF-8 encoded string body with an explicit
/// <c>Content-Type</c> and <c>Content-Length</c>.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.Text"/> / <see cref="Results.Content"/> (or their
/// <see cref="TypedResults"/> counterparts); the constructor is internal so the factories remain
/// the only entry point. The carrier is immutable and may be reused across exchanges.
/// </remarks>
public sealed class ContentHttpResult : IResult
{
    internal ContentHttpResult(string content, string? contentType, HttpStatusCode? statusCode)
    {
        Content = content;
        ContentType = contentType ?? HttpResultDefaults.TextMediaType;
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the response body text.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// Gets the <c>Content-Type</c> this result sets. Defaults to <c>text/plain; charset=utf-8</c>
    /// when the factory was given none.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the status code this result sets, or <see langword="null"/> to leave the response's
    /// current status untouched.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Writes the UTF-8 encoded <see cref="Content"/> to the response body with
    /// <see cref="ContentType"/>, <c>Content-Length</c>, and (when set) <see cref="StatusCode"/>.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the body write.</param>
    /// <returns>A task that completes when the body has been written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        byte[] payload = Encoding.UTF8.GetBytes(Content);
        return HttpResultWriter.WritePayloadAsync(context, StatusCode, ContentType, payload, cancellationToken);
    }
}
