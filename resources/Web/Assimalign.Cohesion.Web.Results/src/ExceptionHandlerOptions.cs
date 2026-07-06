using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Results;

using Assimalign.Cohesion.Http;

/// <summary>
/// Builder-time configuration for the exception-boundary middleware: the ordered handler chain, the
/// developer-detail toggle, the fallback status, and the diagnostics-suppression callback. Everything
/// here is composed once when the pipeline is built and captured by the middleware, so no
/// per-request service resolution occurs.
/// </summary>
public sealed class ExceptionHandlerOptions
{
    /// <summary>
    /// The <see cref="IHttpContext.Items"/> key the boundary sets to <see langword="true"/> when
    /// <see cref="SuppressDiagnosticsCallback"/> marks a caught exception as expected. A diagnostics
    /// middleware reads this key to skip error-level logging for that exchange.
    /// </summary>
    public const string DiagnosticsSuppressedItemKey = "Cohesion.Web.Diagnostics.Suppressed";

    /// <summary>
    /// Gets the ordered exception-handler chain. On a caught exception each handler is tried in this
    /// order until one returns <see langword="true"/>; if none do, the safe problem+json fallback
    /// runs. Populate this at builder time (for example inside
    /// <c>UseExceptionHandler(options =&gt; options.Handlers.Add(...))</c>).
    /// </summary>
    public IList<IExceptionHandler> Handlers { get; } = new List<IExceptionHandler>();

    /// <summary>
    /// Gets or sets a value indicating whether the fallback problem+json response includes developer
    /// detail (the exception message and an <c>exception</c> extension carrying the type and stack
    /// trace). Defaults to <see langword="false"/> so internals never leak in production; a host
    /// typically enables it only in the Development environment.
    /// </summary>
    public bool IncludeDeveloperDetails { get; set; }

    /// <summary>
    /// Gets or sets the status code the fallback problem+json response uses. Defaults to
    /// <see cref="HttpStatusCode.InternalServerError"/> (500).
    /// </summary>
    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.InternalServerError;

    /// <summary>
    /// Gets or sets a predicate that marks a caught exception as expected. When it returns
    /// <see langword="true"/>, the boundary flags the exchange
    /// (<see cref="IHttpContext.Items"/>[<see cref="DiagnosticsSuppressedItemKey"/>] =
    /// <see langword="true"/>) so a diagnostics middleware skips error-level logging for it. The
    /// exception is still handled and a response is still produced. This is the parity seam for the
    /// .NET 10 <c>ExceptionHandlerOptions.SuppressDiagnosticsCallback</c>.
    /// </summary>
    public Func<IHttpContext, Exception, bool>? SuppressDiagnosticsCallback { get; set; }
}
