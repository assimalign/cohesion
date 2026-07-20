using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Builder-time configuration for the exception-boundary middleware (<c>UseErrorHandling</c>): the
/// developer-detail toggle, the fault-observation hook, and the diagnostics-suppression predicate.
/// Everything here is captured once when the pipeline is composed, so the boundary resolves nothing
/// from a service provider at request time.
/// </summary>
public sealed class ExceptionBoundaryOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the boundary's terminal problem+json fallback
    /// includes developer detail — the exception <see cref="Exception.Message"/> as the problem
    /// <c>detail</c> and the full <see cref="Exception.ToString"/> text as an <c>exception</c>
    /// extension member. Defaults to <see langword="false"/> so fault internals never leak in
    /// production; a host typically enables it only in a development environment.
    /// </summary>
    /// <remarks>
    /// The toggle affects only the boundary's own terminal fallback (the one that runs when no
    /// registered <c>OnError</c> handler owns the fault). A handler that owns the fault writes
    /// whatever payload it chooses and is unaffected by this setting.
    /// </remarks>
    public bool IncludeDeveloperDetails { get; set; }

    /// <summary>
    /// Gets or sets the diagnostic-observation hook the boundary invokes for each caught fault — the
    /// seam through which an application records a fault (logging, metrics, tracing) without the
    /// boundary depending on any logging stack. Invoked before the response is shaped, and only when
    /// <see cref="SuppressDiagnosticsCallback"/> does not mark the fault as expected. A hook that
    /// throws is swallowed: fault observation must never defeat the boundary's core job of rendering
    /// the error response.
    /// </summary>
    public Func<IHttpContext, Exception, ValueTask>? OnException { get; set; }

    /// <summary>
    /// Gets or sets a predicate that marks a caught exception as <em>expected</em>. When it returns
    /// <see langword="true"/> the boundary skips <see cref="OnException"/> for that fault (the fault
    /// is still handled and a response is still produced) — the Cohesion parity for the .NET 10
    /// <c>ExceptionHandlerOptions.SuppressDiagnosticsCallback</c> concept, adapted to the boundary's
    /// own diagnostic hook because the repo carries no <c>Microsoft.Extensions.Logging</c>. A
    /// predicate that throws is treated as "do not suppress".
    /// </summary>
    public Func<IHttpContext, Exception, bool>? SuppressDiagnosticsCallback { get; set; }
}
