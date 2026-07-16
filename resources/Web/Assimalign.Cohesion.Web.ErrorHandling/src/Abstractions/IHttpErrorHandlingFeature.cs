using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// The <c>OnError</c> hook as it appears on an exchange: the typed feature through which a
/// pipeline exception boundary turns a fault into the application's error response.
/// </summary>
/// <remarks>
/// <para>
/// The feature is composed once at builder time (<c>AddErrorHandling</c>) and seeded onto every
/// exchange. Boundaries that catch pipeline faults — the exception-boundary middleware, or an
/// inline <c>try/catch</c> around a risky section — resolve it via
/// <c>context.Features.Get&lt;IHttpErrorHandlingFeature&gt;()</c> and delegate the response to
/// <see cref="HandleAsync"/>.
/// </para>
/// <para>
/// <see cref="HandleAsync"/> assumes an unstarted response: callers own the invariant that no
/// response bytes have been committed before invoking the hook (a boundary that buffers or wraps
/// the pipeline enforces this; the hook itself cannot un-send a response head). Response hygiene —
/// clearing headers or status a faulted handler may have half-set — is likewise the boundary's
/// responsibility.
/// </para>
/// </remarks>
public interface IHttpErrorHandlingFeature : IHttpFeature
{
    /// <summary>
    /// Gets the application's <c>OnError</c> registrations, in the order they are consulted. The
    /// terminal <c>ProblemDetails</c> default is not part of the list — it runs only when every
    /// registration passes.
    /// </summary>
    IReadOnlyList<IHttpErrorHandler> Handlers { get; }

    /// <summary>
    /// Turns <paramref name="exception"/> into a response on <paramref name="context"/>: consults
    /// the registered handlers in order, stopping at the first that owns the fault; when none
    /// does, the terminal default writes the RFC 9457 <c>ProblemDetails</c> payload
    /// (<c>500</c>, <c>application/problem+json</c>, no exception detail).
    /// </summary>
    /// <param name="context">The exchange whose pipeline faulted.</param>
    /// <param name="exception">The fault that escaped the pipeline.</param>
    /// <param name="cancellationToken">A token that cancels response writing.</param>
    /// <returns>A task that completes when a handler (or the terminal default) has written the response.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="exception"/> is <see langword="null"/>.</exception>
    ValueTask HandleAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken = default);
}
