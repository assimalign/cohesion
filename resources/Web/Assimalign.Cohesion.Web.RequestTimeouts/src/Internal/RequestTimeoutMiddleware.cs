using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The request-timeout middleware: arms a per-exchange timer (global default policy, overridden
/// by the matched endpoint's <see cref="RequestTimeoutMetadata"/>), hands downstream a context
/// whose <see cref="IHttpContext.RequestCancelled"/> is the timeout-linked token, and translates
/// an expiry-attributable unwind into the configured timeout response — or a clean protocol-level
/// abort when the response has already started.
/// </summary>
/// <remarks>
/// <para>
/// Expiry attribution: a downstream <see cref="OperationCanceledException"/> is converted only
/// when the timeout timer fired <em>and</em> the transport's own
/// <see cref="IHttpContext.RequestCancelled"/> has not — a client-initiated abort therefore
/// propagates unchanged (the server treats it as a clean drain) and is never mislabeled as a
/// timeout. A handler that swallows the cancellation and completes keeps the response it
/// produced, matching ASP.NET.
/// </para>
/// <para>
/// The timeout path deliberately does <b>not</b> trip <see cref="IHttpContext.Cancel"/> while
/// the response is still writable: every transport answers a cancel request by resetting the
/// exchange instead of sending (HTTP/1.1 sends nothing and ends the connection; HTTP/2 and
/// HTTP/3 reset the stream), so the 504 would never reach the client. Cancellation of downstream
/// <em>work</em> rides the linked token instead, and <see cref="IHttpContext.CancelAsync"/> is
/// reserved for the response-already-started path, where a wire reset is the only clean answer.
/// </para>
/// </remarks>
internal sealed class RequestTimeoutMiddleware : IWebApplicationMiddleware
{
    // The status source when a timeout fires with no configured policy — possible only when a
    // handler armed the timer itself through IHttpRequestTimeoutFeature.SetTimeout.
    private static readonly RequestTimeoutPolicy FallbackPolicy = new();

    private readonly RequestTimeoutOptions _options;

    public RequestTimeoutMiddleware(RequestTimeoutOptions options)
    {
        _options = options;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        // Mirrors ASP.NET: a paused debug session must not cancel the request under inspection.
        if (_options.SuspendWhenDebuggerAttached && Debugger.IsAttached)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        HttpRequestTimeoutFeature feature = new(context, _options);

        try
        {
            context.Features.Set<IHttpRequestTimeoutFeature>(feature);

            try
            {
                await next.Invoke(new RequestTimeoutHttpContext(context, feature)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (feature.TimedOut && !context.RequestCancelled.IsCancellationRequested)
            {
                await WriteTimeoutResponseAsync(context, feature.EffectivePolicy ?? FallbackPolicy).ConfigureAwait(false);
            }
        }
        finally
        {
            // Remove before disposing so later pipeline stages can never resolve a feature whose
            // cancellation sources have been released.
            context.Features.Set<IHttpRequestTimeoutFeature>(null);
            feature.Dispose();
        }
    }

    private static async Task WriteTimeoutResponseAsync(IHttpContext context, RequestTimeoutPolicy policy)
    {
        // Headers already committed to the wire (streamed responses): the status can no longer be
        // changed, so the only clean answer is the protocol-level per-exchange abort — HTTP/2 and
        // HTTP/3 reset the stream, HTTP/1.1 truncates and closes after the exchange.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        if (policy.WriteResponse is { } writeResponse)
        {
            await writeResponse.Invoke(context).ConfigureAwait(false);
            return;
        }

        ResetResponse(context.Response, policy.StatusCode);

        if (policy.WriteProblemDetails)
        {
            // The request token is cancelled by definition here — the payload write must not
            // observe it or the timeout response would cancel itself.
            await context.Response
                .WriteProblemDetailsAsync(ProblemDetails.FromStatus(policy.StatusCode), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static void ResetResponse(IHttpResponse response, HttpStatusCode statusCode)
    {
        // The handler may have staged headers and body bytes before it timed out; the timeout
        // response replaces them (the imperative analog of ASP.NET's Response.Clear).
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
}
