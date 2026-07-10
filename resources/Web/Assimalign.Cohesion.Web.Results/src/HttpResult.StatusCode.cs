using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A bodyless result that sets only the response status code.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.StatusCode"/> or <see cref="TypedResults.StatusCode"/>; the
/// constructor is internal so the factories remain the only entry point.
/// </remarks>
public sealed class StatusCodeHttpResult : IResult
{
    internal StatusCodeHttpResult(HttpStatusCode statusCode)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the status code this result sets on the response.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Sets <see cref="StatusCode"/> on the response. No headers or body are written.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the response write.</param>
    /// <returns>A task that completes when the status code has been set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCode;
        return Task.CompletedTask;
    }
}
