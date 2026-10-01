using System;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Routing;

namespace Assimalign.Cohesion.Web.Antiforgery.Internal;

/// <summary>
/// The antiforgery middleware: validates an unsafe-method request to an endpoint whose
/// <see cref="AntiforgeryMetadata"/> requires it, answers a failed validation with
/// <c>400 Bad Request</c> as <c>application/problem+json</c>, and otherwise acknowledges the endpoint and
/// calls <c>next</c>.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint comes from the route match <c>UseRouting</c> publishes before this middleware runs, so
/// the middleware belongs after <c>UseRouting</c>. Registered ahead of it, the middleware sees no
/// endpoint and acknowledges none, and routing's dispatch fails an endpoint that requires validation
/// rather than running it unprotected (<see cref="IRouteMiddlewareMetadata"/>). The candidate endpoint of
/// a CORS preflight never runs for the preflight, so it is neither validated nor acknowledged.
/// </para>
/// <para>
/// Validation uses the exchange's antiforgery service (<c>context.Antiforgery</c>): the one
/// <c>AddAntiforgery</c> registered, unless a middleware replaced it for the exchange, so the service
/// that validates is always the one handlers mint with. The token header is consulted before the body:
/// the form is read only when the request carries no token header and has a form content type, so an
/// endpoint that streams its body keeps it when the client sends the token in the header.
/// </para>
/// </remarks>
internal sealed class AntiforgeryMiddleware : IWebApplicationMiddleware
{
    /// <summary>
    /// The pipeline verb the middleware acknowledges on the endpoint, and the one
    /// <see cref="AntiforgeryMetadata.RequiredMiddleware"/> names.
    /// </summary>
    internal const string Verb = "UseAntiforgery";

    private const string rejectionDetail = "The antiforgery token was missing or invalid.";

    private readonly AntiforgeryFeature _registration;

    public AntiforgeryMiddleware(AntiforgeryFeature registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        _registration = registration;
    }

    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        if (context.GetRouteMatch() is { IsPreflight: false } endpoint)
        {
            if (endpoint.Metadata.GetMetadata<AntiforgeryMetadata>() is { RequiresValidation: true }
                && !IsExempt(context.Request.Method)
                && !await IsValidAsync(context).ConfigureAwait(false))
            {
                // Answer the request here; the endpoint does not run.
                await RejectAsync(context).ConfigureAwait(false);
                return;
            }

            // Acknowledge every endpoint this middleware processed, with or without a requirement of its
            // own: a safe-method request to a protected endpoint passed, and routing checks every metadata
            // item that names this middleware.
            context.AcknowledgeEndpointMiddleware(Verb);
        }

        await next.Invoke(context).ConfigureAwait(false);
    }

    private async Task<bool> IsValidAsync(IHttpContext context)
    {
        IHttpRequest request = context.Request;

        if (!HasHeaderToken(request) && HasFormContentType(request))
        {
            try
            {
                // Parses and caches the form on the exchange, so a form-bound endpoint binds from the same
                // parse instead of reading a consumed body.
                await context.ReadFormAsync(context.RequestCancelled).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                // A body the form reader rejects (malformed, or over the form limits) carries no token the
                // server can verify.
                return false;
            }
        }

        IHttpAntiforgery antiforgery = context.Antiforgery ?? _registration.Antiforgery;
        return await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false);
    }

    private bool HasHeaderToken(IHttpRequest request)
    {
        return request.Headers.TryGetValue(_registration.Options.HeaderName, out HttpHeaderValue token)
            && !string.IsNullOrEmpty(token.Value);
    }

    private static bool HasFormContentType(IHttpRequest request)
    {
        return request.Headers.TryGetValue(HttpHeaderKey.ContentType, out HttpHeaderValue value)
            && HttpMediaType.TryParse(value.Value, out HttpMediaType contentType)
            && (HttpMediaType.FormUrlEncoded.Includes(contentType) || HttpMediaType.MultipartFormData.Includes(contentType));
    }

    // The exempt set is the antiforgery service's own: the bodiless safe methods (RFC 9110 §9.2.1). QUERY is
    // safe (RFC 10008) but carries a body, which is the vector antiforgery defends, so it is validated like
    // POST. Checking it here keeps an exempt request's body unread; the service applies the same set.
    private static bool IsExempt(HttpMethod method)
    {
        return method == HttpMethod.Get
            || method == HttpMethod.Head
            || method == HttpMethod.Options
            || method == HttpMethod.Trace;
    }

    private static async Task RejectAsync(IHttpContext context)
    {
        // The check runs before next, so the head is normally still writable. A middleware ahead of this
        // one may already have committed it, though, and then the status can no longer be set: abort the
        // exchange at the protocol layer instead, so the endpoint still does not run.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        ProblemDetails problem = ProblemDetails.FromStatus(HttpStatusCode.BadRequest, rejectionDetail);
        await context.Response.WriteProblemDetailsAsync(problem, context.RequestCancelled).ConfigureAwait(false);
    }
}
