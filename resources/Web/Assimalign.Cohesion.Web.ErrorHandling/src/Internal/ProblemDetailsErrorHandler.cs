using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// The terminal default of the <c>OnError</c> chain: renders the fault as the RFC 9457
/// <c>ProblemDetails</c> payload — <c>500 Internal Server Error</c>, <c>application/problem+json</c>,
/// and deliberately no exception detail (fault internals never leak to clients by default;
/// applications that want richer payloads register their own handler ahead of this one).
/// </summary>
internal sealed class ProblemDetailsErrorHandler : IHttpErrorHandler
{
    internal static ProblemDetailsErrorHandler Instance { get; } = new();

    private ProblemDetailsErrorHandler()
    {
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.InternalServerError);

        await context.Response.WriteProblemDetailsAsync(problem, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
