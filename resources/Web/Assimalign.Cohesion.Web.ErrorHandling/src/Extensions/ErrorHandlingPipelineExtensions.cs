using System;
using System.Linq;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Pipeline-builder verbs that place the error-handling middleware — the exception boundary and
/// the status-code pages. They compose against the root
/// <see cref="IWebApplicationPipelineBuilder"/> and are dependency-free: each places what its
/// registration verb configured, and the middleware resolves nothing per request.
/// </summary>
/// <remarks>
/// Place <see cref="UseErrorHandling(IWebApplicationPipelineBuilder)"/> <b>first</b> so it wraps every
/// middleware that follows. Configure it with
/// <c>builder.Services.AddErrorHandling(errors =&gt; ...)</c> at builder time: <c>errors.OnError(...)</c>
/// registers fault handlers, which the boundary consults before falling back to the terminal
/// problem+json default, and <c>errors.Options</c> configures the boundary itself.
/// <see cref="UseStatusCodePages(IWebApplicationPipelineBuilder)"/> is independent and opt-in: it upgrades
/// bodyless <c>4xx</c>/<c>5xx</c> terminal responses (such as the pipeline's bodyless 404) into
/// problem+json, or the responder <c>builder.Services.AddStatusCodePages(pages =&gt; ...)</c> configured.
/// </remarks>
public static class ErrorHandlingPipelineExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the pipeline exception boundary at the current point in the pipeline: it catches
        /// faults escaping downstream middleware, publishes the caught exception as an
        /// <see cref="IHttpExceptionFeature"/>, resets an unstarted response (aborting the exchange
        /// instead when the response has already started), and produces the error response through
        /// the application's <c>OnError</c> chain, falling back to a safe problem+json terminal.
        /// Place it near the front so it wraps every middleware that follows. Only
        /// <c>UseHttpLogging</c>, <c>UseSecurityHeaders</c>, <c>UseForwardedHeaders</c>,
        /// <c>UseHostFiltering</c>, <c>UseHttpsRedirection</c>, and <c>UseHsts</c> belong ahead of it:
        /// logging, security headers, and HSTS act on its error responses, and the others establish or
        /// check the request's identity before anything else runs.
        /// </summary>
        /// <remarks>
        /// The boundary is configured where the hook is registered
        /// (<c>builder.Services.AddErrorHandling(errors =&gt; errors.Options...)</c>, see
        /// <see cref="ErrorHandlingBuilder.Options"/>): the developer-detail toggle, the fault-observation
        /// hook, and the diagnostics-suppression predicate. This verb only positions it, reading the
        /// options of the hook the application registered last, or the production defaults (no developer
        /// detail, no observer) when it registered none.
        /// </remarks>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseErrorHandling()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                ExceptionBoundaryOptions options = context.Features.OfType<ErrorHandlingFeature>().LastOrDefault()?.BoundaryOptions
                    ?? new ExceptionBoundaryOptions();
                ExceptionBoundaryMiddleware boundary = new(options);

                return exchange => boundary.InvokeAsync(exchange, next);
            });
        }

        /// <summary>
        /// Places the status-code-pages middleware, which renders a bodyless <c>4xx</c>/<c>5xx</c> terminal
        /// response as RFC 9457 problem+json, or through the custom responder the application registered
        /// with <c>builder.Services.AddStatusCodePages(pages =&gt; ...)</c>. It only acts on a genuinely
        /// bodyless, unstarted response, so it never clobbers a body a handler already wrote.
        /// </summary>
        /// <remarks>
        /// The responder is configured where the middleware is registered
        /// (<see cref="StatusCodePagesBuilder"/>); this verb only positions it. When the application
        /// registered status-code pages more than once, the last registration wins; when it registered
        /// none, bodyless error responses render as problem+json.
        /// </remarks>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStatusCodePages()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<StatusCodePagesMiddleware>().LastOrDefault()
                    ?? new StatusCodePagesBuilder().Build();

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
