using System;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.HttpsPolicy.Internal;

namespace Assimalign.Cohesion.Web.HttpsPolicy;

/// <summary>
/// Pipeline-builder extension that wires HTTP-to-HTTPS redirection into the Web application middleware
/// pipeline.
/// </summary>
public static class HttpsRedirectionExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds middleware that answers an insecure request with a method-preserving redirect to the
        /// same host and request target on the <c>https</c> scheme and the configured HTTPS port, and
        /// lets an already-secure request pass through.
        /// </summary>
        /// <param name="configure">
        /// An optional callback to configure the redirect status (<c>307</c> default, <c>308</c>
        /// permanent) and the HTTPS port (default <c>443</c>). When <see langword="null"/>, the defaults
        /// apply.
        /// </param>
        /// <returns>The same <see cref="IWebApplicationPipelineBuilder"/> for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// The configured status is neither <c>307</c> nor <c>308</c>, or the configured HTTPS port is
        /// outside the range 1–65535.
        /// </exception>
        /// <remarks>
        /// <para>
        /// Register this <em>early</em> — before response-shaping middleware such as compression or
        /// serialization — so an insecure request is redirected and short-circuited before any work is
        /// done on a response that is about to be discarded. The redirect is bodyless (status +
        /// <c>Location</c>).
        /// </para>
        /// <para>
        /// Connection security is the transport-derived typed scheme (from the listener's transport
        /// security capability, #763); there is no scheme-string sniffing. The status and port are
        /// validated here, at builder time, never per request.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseHttpsRedirection(Action<HttpsRedirectionOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            HttpsRedirectionOptions options = new();
            configure?.Invoke(options);

            int status = options.StatusCode.Value;
            if (status is not (307 or 308))
            {
                throw new ArgumentException(
                    $"HTTPS redirection requires a method-preserving status of 307 or 308; got {status}.",
                    nameof(configure));
            }

            if (options.HttpsPort is < 1 or > 65535)
            {
                throw new ArgumentException(
                    $"The HTTPS port must be in the range 1-65535; got {options.HttpsPort}.",
                    nameof(configure));
            }

            return builder.Use(new HttpsRedirectionMiddleware(options.StatusCode, options.HttpsPort));
        }
    }
}
