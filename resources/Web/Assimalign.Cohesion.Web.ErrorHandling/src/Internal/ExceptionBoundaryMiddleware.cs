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
/// <b>Client faults are not application faults.</b> When the server reports that the client's request
/// was at fault (<see cref="IWebClientFaultFeature"/>: reading a body that broke its framing or a
/// configured limit failed, and the transport answers the exchange itself, #1340), the boundary still
/// publishes the <see cref="IHttpExceptionFeature"/> but skips the
/// <see cref="ExceptionBoundaryOptions.OnException"/> hook and the <c>OnError</c> handlers, and stages
/// the transport's status with an empty body in place of the <c>500</c> problem. A started response is
/// aborted as for any other fault.
/// </para>
/// <para>
/// <b>Handler faults propagate.</b> An exception thrown by a registered <see cref="IErrorHandler"/> is
/// not masked — it surfaces out of the boundary to the server's last-resort connection isolation
/// (#762), which keeps the connection alive without invoking this hook. The diagnostic-observation
/// hook (<see cref="ExceptionBoundaryOptions.OnException"/>) is the exception: its failure is swallowed,
/// because fault observation must never defeat the boundary's core job of rendering the response.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ExceptionBoundaryMiddleware")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class ExceptionBoundaryMiddleware : IWebApplicationMiddleware
{
    private static readonly string _responseAbandonedByClientMessage = ErrorMessages.ResponseAbandonedByClient;

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
        // The transport's latched outcome survives middleware wrapping its original exception.
        // This boundary classifies arbitrary downstream exceptions, so the filter deliberately
        // covers every exception type once the response was abandoned by its client.
        catch (Exception exception) when (context.Features.Get<IWebResponseClientFaultFeature>() is { ResponseAbortedByClient: true })
        {
            ErrorHandlingEventSource.Log.ResponseAbortedByPeer(context, exception);
            await context.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(_responseAbandonedByClientMessage, exception, context.RequestCancelled);
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

        // The server reports that the client's request was at fault: reading its body failed on the
        // client's side, and the transport answers the exchange itself (#1340). That is an expected
        // protocol outcome, not an application defect, so the fault observer does not see it.
        HttpStatusCode? clientFault = context.Features.Get<IWebClientFaultFeature>()?.StatusCode;
        bool responseStarted = context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true };
        if (clientFault is null)
        {
            // The catch path already classified this as an application fault. Later callbacks and
            // handlers can cancel the exchange or encounter a separate client fault; neither changes
            // the cause of this failure. Share the one suppression result with both reporting paths.
            bool suppressed = ShouldSuppressDiagnostics(context, exception);
            ErrorHandlingEventSource.Log.PipelineFault(context, exception, responseStarted, suppressed);
            await ObserveAsync(context, exception, suppressed).ConfigureAwait(false);
        }

        // No-clobber: a callback may have started the response since the original fault was reported.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            if (clientFault is null && !context.RequestCancelled.IsCancellationRequested &&
                context.Features.Get<IWebResponseClientFaultFeature>() is not { ResponseAbortedByClient: true })
            {
                ErrorHandlingEventSource.Log.ResponseAborted();
            }

            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        if (clientFault is { } status)
        {
            // Stage the transport's own answer, with no body: no 500 problem, and no OnError handler,
            // since those own application faults. Staging the status the transport sends keeps every
            // reader downstream of the boundary (an access log, the server's telemetry) on the status
            // that reaches the wire.
            ResetResponse(context.Response, status);
            return;
        }

        ResetResponse(context.Response, HttpStatusCode.InternalServerError);

        // Give the application's OnError registrations first crack, in registration order — the
        // first to own the fault stops the chain. A handler that throws is not masked.
        if (context.Features.Get<IErrorHandlingFeature>() is { } hook)
        {
            for (int index = 0; index < hook.Handlers.Count; index++)
            {
                IErrorHandler handler = hook.Handlers[index];
                try
                {
                    if (await ErrorHandlerInvoker.InvokeAsync(handler, context, exception, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                // Deviates from the specific-catch rule: a false filter observes any handler
                // failure without catching it or changing its propagation.
                catch (Exception failure) when (ErrorHandlingEventSource.Log.ErrorHandlerFailed(context, index, handler, failure))
                {
                    throw;
                }
            }
        }

        await WriteTerminalAsync(context, exception, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Invokes the diagnostic-observation hook for a caught fault, unless the suppression predicate
    /// marks the fault as expected.
    /// </summary>
    private async ValueTask ObserveAsync(IHttpContext context, Exception exception, bool suppressed)
    {
        if (suppressed)
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
            catch (Exception failure)
            {
                ErrorHandlingEventSource.Log.DiagnosticsCallbackFailed(context, nameof(ExceptionBoundaryOptions.OnException), failure);
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
        catch (Exception failure)
        {
            ErrorHandlingEventSource.Log.DiagnosticsCallbackFailed(context, nameof(ExceptionBoundaryOptions.SuppressDiagnosticsCallback), failure);
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
    /// is replaced outright); the status is set to <paramref name="statusCode"/>: a 500 baseline that a
    /// handler or the terminal may override, or the status the transport answers a client fault with.
    /// </summary>
    private static void ResetResponse(IHttpResponse response, HttpStatusCode statusCode)
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

        response.StatusCode = statusCode;
    }

    private sealed class DebugView
    {
        public DebugView(ExceptionBoundaryMiddleware value)
        {
        }
    }
}
