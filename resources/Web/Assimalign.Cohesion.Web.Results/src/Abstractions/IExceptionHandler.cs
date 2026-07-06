using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// A single link in the exception-boundary handler chain. Handlers are registered at builder time and
/// tried in registration order; the first one to return <see langword="true"/> owns the response and
/// stops the chain. When every handler returns <see langword="false"/> the boundary falls back to a
/// safe problem+json response.
/// </summary>
/// <remarks>
/// This mirrors the .NET <c>IExceptionHandler</c> shape but is composed at builder time rather than
/// resolved from a service provider per request, honoring the Cohesion rule that pipeline
/// extensibility is builder-time composition, not request-time service location.
/// </remarks>
public interface IExceptionHandler
{
    /// <summary>
    /// Attempts to handle <paramref name="exception"/> by writing a response.
    /// </summary>
    /// <param name="context">The HTTP context whose pipeline faulted. The caught exception is also
    /// available as an <see cref="IHttpExceptionFeature"/> on <see cref="IHttpContext.Features"/>.</param>
    /// <param name="exception">The exception the boundary caught.</param>
    /// <param name="cancellationToken">A token that cancels the handling operation.</param>
    /// <returns><see langword="true"/> if this handler produced the response and the chain should
    /// stop; otherwise <see langword="false"/> to defer to the next handler or the fallback.</returns>
    ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken);
}
