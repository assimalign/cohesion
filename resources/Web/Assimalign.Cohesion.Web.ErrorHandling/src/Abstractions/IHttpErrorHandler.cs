using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// One registration on the application's <c>OnError</c> hook: inspects a fault that escaped the
/// pipeline and either owns the response for it or passes.
/// </summary>
/// <remarks>
/// <para>
/// Handlers are registered at builder time (<see cref="ErrorHandlingBuilder.OnError(IHttpErrorHandler)"/>)
/// and consulted in registration order by <see cref="IHttpErrorHandlingFeature.HandleAsync"/> —
/// the first handler to return <see langword="true"/> ends the chain, so specific handlers
/// register before general ones. A handler that returns <see langword="true"/> must have written
/// (or deliberately chosen) the response. When every handler passes, the terminal default renders
/// the RFC 9457 <c>ProblemDetails</c> payload.
/// </para>
/// <para>
/// The hook is for <em>faults</em> — exceptions thrown by feature libraries and application code
/// (an unavailable key ring, a missing serialization contract, an IO failure). Expected protocol
/// <em>outcomes</em> (an authentication challenge's <c>401</c>, a router's <c>404</c>, an
/// unsupported media type's <c>415</c>) are each feature's normal response path and must never
/// arrive here as exceptions.
/// </para>
/// <para>
/// Implementations are shared across concurrent exchanges and must be thread-safe. Exceptions a
/// handler itself throws are not swallowed — they propagate to the invoking boundary, behind
/// which the server's last-resort connection isolation still stands.
/// </para>
/// </remarks>
public interface IHttpErrorHandler
{
    /// <summary>
    /// Attempts to turn <paramref name="exception"/> into a response on <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The exchange whose pipeline faulted.</param>
    /// <param name="exception">The fault that escaped the pipeline.</param>
    /// <param name="cancellationToken">A token that cancels response writing.</param>
    /// <returns>
    /// <see langword="true"/> when this handler owned the fault and the response is complete;
    /// <see langword="false"/> to pass the fault to the next registration.
    /// </returns>
    ValueTask<bool> TryHandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default);
}
