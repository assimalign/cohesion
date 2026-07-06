using System;
using System.IO;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Results.Internal;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

/// <summary>
/// The last-chance exception boundary. Wraps the rest of the pipeline in a guard that, on a fault,
/// publishes the caught exception as an <see cref="IHttpExceptionFeature"/>, runs the builder-time
/// handler chain, and otherwise resets the response to a safe problem+json body. Without it an
/// unhandled middleware exception escapes into the transport, which aborts the connection and can
/// leak internals.
/// </summary>
/// <remarks>
/// The middleware is composed once at builder time from a captured <see cref="ExceptionHandlerOptions"/>;
/// it resolves nothing from a service provider at request time, honoring the Lane E guardrail that
/// pipeline extensibility flows through builder-time composition and typed features.
/// </remarks>
internal sealed class ExceptionHandlerMiddleware : IWebApplicationMiddleware
{
    private readonly ExceptionHandlerOptions _options;

    public ExceptionHandlerMiddleware(ExceptionHandlerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
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
        // Deviates from AGENTS.md "catch specific exceptions" per design decision: a last-chance
        // exception boundary must intercept every fault to keep it from escaping into the transport.
        catch (Exception exception)
        {
            if (!await TryHandleAsync(context, exception).ConfigureAwait(false))
            {
                // The response was already committed to the wire, so no clean error body can be
                // written. Re-throw (preserving the stack) to the server's per-connection isolation
                // boundary, which aborts only this connection.
                throw;
            }
        }
    }

    private async Task<bool> TryHandleAsync(IHttpContext context, Exception exception)
    {
        // Publish the fault as a typed feature so handlers, diagnostics, and custom pages can read it.
        context.Features.Set<IHttpExceptionFeature>(new HttpExceptionFeature(exception, context.Request.Path));

        if (ShouldSuppressDiagnostics(context, exception))
        {
            context.Items[ExceptionHandlerOptions.DiagnosticsSuppressedItemKey] = true;
        }

        if (!TryResetResponse(context.Response))
        {
            return false;
        }

        foreach (IExceptionHandler handler in _options.Handlers)
        {
            try
            {
                if (await handler.TryHandleAsync(context, exception, context.RequestCancelled).ConfigureAwait(false))
                {
                    return true;
                }
            }
            // A faulty handler must not defeat the last-chance boundary; fall through to the fallback.
            catch (Exception)
            {
            }
        }

        await WriteFallbackAsync(context, exception).ConfigureAwait(false);
        return true;
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
        // A faulty predicate must not break the boundary; treat it as "do not suppress".
        catch (Exception)
        {
            return false;
        }
    }

    private async Task WriteFallbackAsync(IHttpContext context, Exception exception)
    {
        ProblemDetails problem = ProblemDetails.FromStatus(_options.StatusCode);

        if (_options.IncludeDeveloperDetails)
        {
            // Development-only: echo the message and full exception text. Off by default so internals
            // never leak in production.
            problem.Detail = exception.Message;
            problem.Extensions["exception"] = exception.ToString();
        }

        await context.Response.WriteProblemDetailsAsync(problem, context.RequestCancelled).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort discard of any partial response an inner middleware produced before it faulted, so
    /// the error body is clean. A seekable response body has not yet flushed to the wire and can be
    /// truncated; a non-seekable body already carrying content markers cannot, mirroring ASP.NET
    /// Core's "response has already started" bail.
    /// </summary>
    private static bool TryResetResponse(IHttpResponse response)
    {
        Stream body = response.Body;

        if (body.CanSeek)
        {
            body.SetLength(0);
            response.Headers.Clear();
            return true;
        }

        if (response.Headers.ContainsKey(HttpHeaderKey.ContentType) ||
            response.Headers.ContainsKey(HttpHeaderKey.ContentLength))
        {
            return false;
        }

        response.Headers.Clear();
        return true;
    }
}
