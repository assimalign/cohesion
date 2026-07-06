using System;

namespace Assimalign.Cohesion.Web;

using Assimalign.Cohesion.Web.Results;
using Assimalign.Cohesion.Web.Results.Internal;

/// <summary>
/// Pipeline-builder extensions that register the Web.Results middleware. Declared in the shared
/// <see cref="Assimalign.Cohesion.Web"/> namespace (mirroring <c>UseForms()</c>) so a single
/// <c>using Assimalign.Cohesion.Web;</c> surfaces the <c>UseXxx()</c> registration verbs alongside
/// the rest of the pipeline API.
/// </summary>
public static class WebApplicationPipelineExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the last-chance exception boundary at the current point in the pipeline. Register it
        /// first so it wraps every middleware that follows.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseExceptionHandler()
        {
            return builder.UseExceptionHandler(configure: null);
        }

        /// <summary>
        /// Adds the last-chance exception boundary configured by <paramref name="configure"/>: an
        /// ordered <see cref="IExceptionHandler"/> chain, the developer-detail toggle, the fallback
        /// status, and the diagnostics-suppression callback. Register it first so it wraps every
        /// middleware that follows.
        /// </summary>
        /// <param name="configure">A builder-time callback to populate the
        /// <see cref="ExceptionHandlerOptions"/>, or <see langword="null"/> for defaults.</param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseExceptionHandler(Action<ExceptionHandlerOptions>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);

            ExceptionHandlerOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new ExceptionHandlerMiddleware(options));
        }

        /// <summary>
        /// Adds status-code-pages middleware that renders bodyless 4xx/5xx terminal responses as
        /// RFC 9457 problem+json.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStatusCodePages()
        {
            return builder.UseStatusCodePages(configure: null);
        }

        /// <summary>
        /// Adds status-code-pages middleware configured by <paramref name="configure"/>. With no
        /// custom responder it renders bodyless 4xx/5xx terminal responses as RFC 9457 problem+json.
        /// </summary>
        /// <param name="configure">A builder-time callback to populate the
        /// <see cref="StatusCodePagesOptions"/>, or <see langword="null"/> for the problem+json
        /// default.</param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStatusCodePages(Action<StatusCodePagesOptions>? configure)
        {
            ArgumentNullException.ThrowIfNull(builder);

            StatusCodePagesOptions options = new();
            configure?.Invoke(options);

            return builder.Use(new StatusCodePagesMiddleware(options));
        }

        /// <summary>
        /// Adds status-code-pages middleware that renders bodyless 4xx/5xx terminal responses as
        /// RFC 9457 problem+json &#8212; the named, explicit form of <see cref="UseStatusCodePages()"/>.
        /// </summary>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseStatusCodePagesWithProblemDetails()
        {
            return builder.UseStatusCodePages(configure: null);
        }
    }
}
