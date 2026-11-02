using System;
using System.Linq;

namespace Assimalign.Cohesion.Web.Diagnostics;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Pipeline-builder members that place HTTP request/response logging in a web application's pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The logger and the options are supplied where the middleware is registered
/// (<c>builder.Services.AddHttpLogging(logging =&gt; ...)</c>, <see cref="HttpLoggingBuilder"/>) and frozen
/// there — the middleware never performs request-time service location. Emission rides the application's
/// Cohesion logging pipeline: the entries fan out to every registered
/// <see cref="ILoggerProvider"/> (console, debug, the <see cref="W3CAccessLogProvider"/>, ...)
/// subject to the factory's filter rules.
/// </para>
/// <para>
/// <b>Ordering.</b> Place HTTP logging <em>first</em>, ahead of authentication, CORS, and
/// routing, so every exchange is logged — including the ones those middleware reject. Anything
/// placed before it is invisible to the access log. Behind a proxy, place
/// <c>UseForwardedHeaders</c> after it (and after <c>UseSecurityHeaders</c>, when that is
/// placed): the entry reads the effective scheme, host, and
/// client address when it is emitted, after the pipeline unwinds, so it records the forwarded
/// client even though logging runs first. Per-endpoint <see cref="HttpLoggingMetadata"/>
/// overrides are read at the same point, from the endpoint <c>UseRouting</c> published, so they
/// apply although logging runs ahead of routing. When bodies are captured
/// (<see cref="HttpLoggingFields.RequestBody"/> / <see cref="HttpLoggingFields.ResponseBody"/>),
/// remember the captured bytes are whatever crosses the wire at this position in the pipeline —
/// place logging <em>after</em> a decompression middleware to capture decoded payloads, before
/// it to capture the raw ones.
/// </para>
/// </remarks>
public static class WebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the HTTP request/response logging middleware the application registered with
        /// <c>builder.Services.AddHttpLogging(logging =&gt; ...)</c>. Each exchange emits one structured
        /// entry (fields per <see cref="HttpLoggingOptions.Fields"/>, header values redacted outside the
        /// configured allowlists) through the registered logger when the downstream pipeline completes or
        /// faults.
        /// </summary>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// The logger and the options are configured where the middleware is registered
        /// (<see cref="HttpLoggingBuilder"/>); this verb only positions it. There is no default logger, so
        /// composing the pipeline fails with an <see cref="InvalidOperationException"/> when the application
        /// registered none. When it registered more than one, the last registration wins.
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHttpLogging()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                IWebApplicationMiddleware middleware = context.Middleware.OfType<HttpLoggingMiddleware>().LastOrDefault()
                    ?? throw new InvalidOperationException(ErrorMessages.HttpLoggingNotRegistered);

                return exchange => middleware.InvokeAsync(exchange, next);
            });
        }
    }
}
