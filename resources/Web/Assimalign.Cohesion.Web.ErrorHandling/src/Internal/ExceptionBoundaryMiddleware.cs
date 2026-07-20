using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.ErrorHandling.Internal;

/// <summary>
/// The pipeline exception boundary: wraps everything downstream in a guard that, on a fault,
/// publishes the caught exception as an <see cref="IHttpExceptionFeature"/>, gives the application's
/// <c>OnError</c> registrations first crack at the response, and otherwise renders a safe problem+json
/// terminal. Without it an unhandled middleware exception escapes into the transport — connection
/// teardown, or internals leaking onto the wire.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>consumer</em> of the shipped <c>OnError</c> chain (#864), not a second handler
/// abstraction: it reads <see cref="IErrorHandlingFeature.Handlers"/> from the exchange and consults
/// them in registration order, first-<see langword="true"/>-wins, exactly as
/// <see cref="IErrorHandlingFeature.HandleAsync"/> does. It owns its own terminal (rather than calling
/// <c>HandleAsync</c> blindly) only so the developer-detail toggle can enrich the fallback payload —
/// the chain's own terminal has no knobs by design. With the toggle off, the terminal is byte-identical
/// to the chain's default (500, <c>application/problem+json</c>, <c>about:blank</c>, no detail).
/// </para>
/// <para>
/// <b>No-clobber.</b> Once the response head is on the wire (<see cref="IHttpResponseStreamingFeature.HasStarted"/>),
/// the status and headers are locked and no clean error body can replace what a faulted handler began
/// streaming; the only honest answer is a protocol-level abort of this one exchange
/// (<see cref="IHttpContext.CancelAsync"/> — HTTP/2 and HTTP/3 reset the stream, HTTP/1.1 truncates and
/// closes after the exchange). While the response is still unstarted the boundary discards whatever
/// partial response the faulted handler staged and writes the error cleanly.
/// </para>
/// <para>
/// <b>Handler faults propagate.</b> An exception thrown by a registered <see cref="IErrorHandler"/> is
/// not masked — it surfaces out of the boundary to the server's last-resort connection isolation
/// (#762), which keeps the connection alive without invoking this hook. The diagnostic-observation
/// hook (<see cref="ExceptionBoundaryOptions.OnException"/>) is the exception: its failure is swallowed,
/// because fault observation must never defeat the boundary's core job of rendering the response.
/// </para>
/// </remarks>
internal sealed class ExceptionBoundaryMiddleware : IWebApplicationMiddleware
{
    private readonly ExceptionBoundaryOptions _options;

    public ExceptionBoundaryMiddleware(ExceptionBoundaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestCancelled.IsCancellationRequested)
        {
            // A cancelled request is a clean drain, not a fault — let the server observe it and end
            // the exchange without manufacturing an error response.
            throw;
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: a pipeline
        // exception boundary must intercept every fault to keep it from escaping into the transport.
        catch (Exception exception)
        {
            await HandleAsync(context, exception).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(IHttpContext context, Exception exception)
    {
        CancellationToken cancellationToken = context.RequestCancelled;

        // Publish the fault as a typed feature so handlers, a diagnostics observer, and custom pages
        // can read it without it being re-thrown or passed out of band.
        context.Features.Set<IHttpExceptionFeature>(new HttpExceptionFeature(exception, context.Request.Path));

        await ObserveAsync(context, exception).ConfigureAwait(false);

        // No-clobber: a started response can no longer be reshaped, so abort this one exchange.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        ResetResponse(context.Response);

        // Give the application's OnError registrations first crack, in registration order — the
        // first to own the fault stops the chain. A handler that throws is not masked.
        if (context.Features.Get<IErrorHandlingFeature>() is { } hook)
        {
            foreach (IErrorHandler handler in hook.Handlers)
            {
                if (await handler.TryHandleAsync(context, exception, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        await WriteTerminalAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invokes the diagnostic-observation hook for a caught fault, unless the suppression predicate
    /// marks the fault as expected.
    /// </summary>
    private async ValueTask ObserveAsync(IHttpContext context, Exception exception)
    {
        if (ShouldSuppressDiagnostics(context, exception))
        {
            return;
        }

        if (_options.OnException is { } onException)
        {
            try
            {
                await onException.Invoke(context, exception).ConfigureAwait(false);
            }
            // Deviates from the repo "catch specific exceptions" rule per design decision: the
            // diagnostic-observation hook is best-effort and must never defeat response rendering.
            catch (Exception)
            {
            }
        }
    }

    private bool ShouldSuppressDiagnostics(IHttpContext context, Exception exception)
    {
        if (_options.SuppressDiagnosticsCallback is not { } callback)
        {
            return false;
        }

        try
        {
            return callback(context, exception);
        }
        // Deviates from the repo "catch specific exceptions" rule per design decision: a faulty
        // suppression predicate must not break the boundary; treat it as "do not suppress".
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The boundary's terminal fallback, run when no registered handler owned the fault. Reuses the
    /// shipped <c>OnError</c> chain default (500 problem+json, no detail) unless the developer-detail
    /// toggle is enabled, in which case it augments the payload with the exception message and text.
    /// </summary>
    private async Task WriteTerminalAsync(IHttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (!_options.IncludeDeveloperDetails)
        {
            await ProblemDetailsErrorHandler.Instance
                .TryHandleAsync(context, exception, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.InternalServerError);
        problem.Detail = exception.Message;
        problem.Extensions["exception"] = exception.ToString();

        await context.Response.WriteProblemDetailsAsync(problem, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards whatever partial response a faulted handler staged before it threw, so the error body
    /// is written onto a clean slate. Mirrors the request-timeout middleware's reset: an unstarted
    /// response is still buffered, so headers clear and a seekable body truncates (a non-seekable body
    /// is replaced outright); the status is set to a 500 baseline that a handler or the terminal may
    /// override.
    /// </summary>
    private static void ResetResponse(IHttpResponse response)
    {
        response.Headers.Clear();

        if (response.Body.CanSeek)
        {
            response.Body.SetLength(0);
        }
        else
        {
            response.Body = new MemoryStream();
        }

        response.StatusCode = HttpStatusCode.InternalServerError;
    }
}
