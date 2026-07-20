using System;

using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.ErrorHandling.Internal;

namespace Assimalign.Cohesion.Web.ErrorHandling;

/// <summary>
/// Pipeline-builder verbs that register the error-handling middleware — the exception boundary and
/// the status-code pages. They compose against the root
/// <see cref="IWebApplicationPipelineBuilder"/> and are dependency-free: options are captured at
/// builder time and the middleware resolves nothing per request.
/// </summary>
/// <remarks>
/// Register <see cref="UseErrorHandling(IWebApplicationPipelineBuilder, Action{ExceptionBoundaryOptions})"/>
/// <b>first</b> so it wraps every middleware that follows. Pair it with
/// <c>builder.AddErrorHandling().OnError(...)</c> at builder time to register fault handlers; the
/// boundary consults them and falls back to the terminal problem+json default when none owns the
/// fault. <see cref="UseStatusCodePages(IWebApplicationPipelineBuilder, Action{StatusCodePagesOptions})"/>
/// is independent and opt-in: it upgrades bodyless <c>4xx</c>/<c>5xx</c> terminal responses (such as
/// the pipeline's bodyless 404) into problem+json.
/// </remarks>
public static class ErrorHandlingPipelineExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the pipeline exception boundary at the current point in the pipeline: it catches
        /// faults escaping downstream middleware, publishes the caught exception as an
        /// <see cref="IHttpExceptionFeature"/>, resets an unstarted response (aborting the exchange
        /// instead when the response has already started), and produces the error response through
        /// the application's <c>OnError</c> chain, falling back to a safe problem+json terminal.
        /// Register it first so it wraps every middleware that follows.
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the boundary — the developer-detail toggle, the
        /// fault-observation hook, and the diagnostics-suppression predicate. When
        /// <see langword="null"/>, the boundary runs with production defaults (no developer detail,
        /// no observer).
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseErrorHandling(Action<ExceptionBoundaryOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            ExceptionBoundaryOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new ExceptionBoundaryMiddleware(options));
        }

        /// <summary>
        /// Adds status-code-pages middleware that renders a bodyless <c>4xx</c>/<c>5xx</c> terminal
        /// response as RFC 9457 problem+json (or through a custom responder). It only acts on a
        /// genuinely bodyless, unstarted response, so it never clobbers a body a handler already
        /// wrote.
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the middleware — chiefly a custom responder in place of
        /// the problem+json default. When <see langword="null"/>, bodyless error responses render as
        /// problem+json.
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStatusCodePages(Action<StatusCodePagesOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            StatusCodePagesOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new StatusCodePagesMiddleware(options));
        }
    }
}
