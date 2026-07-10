using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A result that sets <c>204 No Content</c> (RFC 9110 §15.3.5) and writes no body.
/// </summary>
/// <remarks>
/// Created through <see cref="Results.NoContent"/> or <see cref="TypedResults.NoContent"/>; the
/// constructor is internal so the factories remain the only entry point. The type is stateless, so
/// one shared instance serves every exchange.
/// </remarks>
public sealed class NoContentHttpResult : IResult
{
    /// <summary>The shared, stateless instance.</summary>
    internal static NoContentHttpResult Instance { get; } = new();

    private NoContentHttpResult()
    {
    }

    /// <summary>
    /// Gets the status code this result sets on the response: <c>204 No Content</c>.
    /// </summary>
    public HttpStatusCode StatusCode => HttpStatusCode.NoContent;

    /// <summary>
    /// Sets <c>204 No Content</c> on the response. No headers or body are written.
    /// </summary>
    /// <param name="context">The HTTP exchange to write the response onto.</param>
    /// <param name="cancellationToken">A token that cancels the response write.</param>
    /// <returns>A task that completes when the status code has been set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public Task ExecuteAsync(IHttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = HttpStatusCode.NoContent;
        return Task.CompletedTask;
    }
}
