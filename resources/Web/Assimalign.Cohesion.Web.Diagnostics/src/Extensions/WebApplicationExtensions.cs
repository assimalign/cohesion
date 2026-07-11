using System;

namespace Assimalign.Cohesion.Web.Diagnostics;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

/// <summary>
/// Pipeline-builder members that add HTTP request/response logging to a web application.
/// </summary>
/// <remarks>
/// <para>
/// The logger is supplied explicitly and the options are frozen at composition time — the
/// middleware never performs request-time service location. Emission rides the application's
/// Cohesion logging pipeline: the entries fan out to every registered
/// <see cref="ILoggerProvider"/> (console, debug, the <see cref="W3CAccessLogProvider"/>, ...)
/// subject to the factory's filter rules.
/// </para>
/// <para>
/// <b>Ordering.</b> Register HTTP logging <em>first</em>, ahead of authentication, CORS, and
/// routing, so every exchange is logged — including the ones those middleware reject. Anything
/// registered before it is invisible to the access log. When bodies are captured
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
        /// Adds HTTP request/response logging to the pipeline. Each exchange emits one
        /// structured entry (fields per <see cref="HttpLoggingOptions.Fields"/>, header values
        /// redacted outside the configured allowlists) through <paramref name="logger"/> when
        /// the downstream pipeline completes or faults.
        /// </summary>
        /// <param name="logger">
        /// The composed logger the entries are written to — typically
        /// <c>ILoggerFactory.Create(options.Category)</c> on the application's logger factory.
        /// </param>
        /// <param name="configure">An optional callback that configures the logging options.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured options are invalid (empty category or redaction placeholder, or <see cref="LogLevel.None"/> level).</exception>
        /// <exception cref="ArgumentOutOfRangeException">A configured body-capture limit is negative.</exception>
        public IWebApplicationPipelineBuilder UseHttpLogging(ILogger logger, Action<HttpLoggingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(logger);

            var options = new HttpLoggingOptions();
            configure?.Invoke(options);

            return builder.Use(new HttpLoggingMiddleware(logger, HttpLoggingSnapshot.Create(options)));
        }

        /// <summary>
        /// Adds HTTP request/response logging to the pipeline, resolving the logger for the
        /// configured <see cref="HttpLoggingOptions.Category"/> from
        /// <paramref name="loggerFactory"/> at composition time.
        /// </summary>
        /// <param name="loggerFactory">The application's logger factory.</param>
        /// <param name="configure">An optional callback that configures the logging options.</param>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">The configured options are invalid (empty category or redaction placeholder, or <see cref="LogLevel.None"/> level).</exception>
        /// <exception cref="ArgumentOutOfRangeException">A configured body-capture limit is negative.</exception>
        public IWebApplicationPipelineBuilder UseHttpLogging(ILoggerFactory loggerFactory, Action<HttpLoggingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            var options = new HttpLoggingOptions();
            configure?.Invoke(options);

            HttpLoggingSnapshot snapshot = HttpLoggingSnapshot.Create(options);

            return builder.Use(new HttpLoggingMiddleware(loggerFactory.Create(snapshot.Category), snapshot));
        }
    }
}
