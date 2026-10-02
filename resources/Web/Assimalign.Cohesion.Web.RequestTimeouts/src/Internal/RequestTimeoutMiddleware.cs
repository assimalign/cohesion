using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.RequestTimeouts.Internal;

/// <summary>
/// The request-timeout middleware: arms a per-exchange timer (the global default policy, replaced
/// by the published endpoint's <see cref="RequestTimeoutMetadata"/>), hands downstream a context
/// whose <see cref="IHttpContext.RequestCancelled"/> is the timeout-linked token, and translates
/// an expiry-attributable unwind into the configured timeout response — or a clean protocol-level
/// abort when the response has already started.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint policy comes from the route match <c>UseRouting</c> publishes before this middleware
/// runs, so the middleware belongs after <c>UseRouting</c>. It acknowledges every non-preflight
/// endpoint it processes, including while suspended for a debugger (enforcement is suspended there, not
/// missing). Registered ahead of <c>UseRouting</c> it sees no endpoint: the global default governs every
/// request, and routing's dispatch fails an endpoint whose metadata carries a timeout rather than
/// running it unbounded (<see cref="IRouteMiddlewareMetadata"/>). The candidate endpoint of a CORS
/// preflight never runs for the preflight, so its policy is neither applied nor acknowledged.
/// </para>
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
    /// <summary>
    /// The pipeline verb the middleware acknowledges on the endpoint, and the one
    /// <see cref="RequestTimeoutMetadata.RequiredMiddleware"/> names.
    /// </summary>
    internal const string Verb = "UseRequestTimeouts";

    // The status source when a timeout fires with no configured policy — possible only when a
    // handler armed the timer itself through IRequestTimeoutFeature.SetTimeout.
    private static readonly RequestTimeoutPolicy _fallbackPolicy = new();

    private readonly RequestTimeoutOptions _options;

    public RequestTimeoutMiddleware(RequestTimeoutOptions options)
    {
        _options = options;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        // The endpoint UseRouting published ahead of this middleware; null when routing selected none
        // (404, 405), when routing runs later in the pipeline, or for a CORS preflight, whose candidate
        // endpoint never runs.
        IRouteMatchFeature? endpoint = context.GetRouteMatch() is { IsPreflight: false } match ? match : null;

        // Mirrors ASP.NET: a paused debug session must not cancel the request under inspection.
        if (_options.SuspendWhenDebuggerAttached && Debugger.IsAttached)
        {
            // Suspended, not missing: acknowledge so an endpoint with a timeout still runs.
            Acknowledge(context, endpoint);
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        // An endpoint policy replaces the global default outright, a disabled one included.
        RequestTimeoutPolicy? policy = endpoint?.Metadata.GetMetadata<RequestTimeoutMetadata>()?.Policy ?? _options.DefaultPolicy;
        RequestTimeoutFeature feature = new(context, policy, _options.TimeProvider);

        try
        {
            context.Features.Set<IRequestTimeoutFeature>(feature);
            Acknowledge(context, endpoint);

            try
            {
                await next.Invoke(new RequestTimeoutHttpContext(context, feature)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (feature.TimedOut && !context.RequestCancelled.IsCancellationRequested)
            {
                await WriteTimeoutResponseAsync(context, feature.EffectivePolicy ?? _fallbackPolicy).ConfigureAwait(false);
            }
        }
        finally
        {
            // Remove before disposing so later pipeline stages can never resolve a feature whose
            // cancellation sources have been released.
            context.Features.Set<IRequestTimeoutFeature>(null);
            feature.Dispose();
        }
    }

    // Records that this middleware processed the endpoint, with or without a policy of its own: routing
    // checks every metadata item that names this middleware, including a group-level timeout an
    // endpoint-level override replaced.
    private static void Acknowledge(IHttpContext context, IRouteMatchFeature? endpoint)
    {
        if (endpoint is not null)
        {
            context.AcknowledgeEndpointMiddleware(Verb);
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
