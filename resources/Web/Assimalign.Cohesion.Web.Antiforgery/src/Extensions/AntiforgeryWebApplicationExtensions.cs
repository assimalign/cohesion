using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Antiforgery.Internal;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// Pipeline installation of antiforgery (CSRF) protection.
/// </summary>
/// <remarks>
/// <para>
/// <c>builder.Services.AddAntiforgery(...)</c>, a component integration the application's compilation
/// receives (<see cref="AntiforgeryComponents"/>, owner decision 34), creates the application's antiforgery
/// service once, over the
/// <c>Assimalign.Cohesion.Http.Antiforgery</c> signed double-submit engine, and registers it as an
/// application feature: every exchange carries it, so handlers mint tokens with
/// <c>context.RequireAntiforgery.GetAndStoreTokens(context)</c>. <c>UseAntiforgery</c> adds the middleware
/// that validates protected endpoints. Composition is dependency-free: the service is a value registered
/// as an <see cref="IHttpFeature"/> singleton, and this package takes no service-container,
/// configuration-binding or hosting reference.
/// </para>
/// <para>
/// Register <c>UseAntiforgery</c> after <c>UseRouting</c>, which publishes the endpoint and its
/// <see cref="AntiforgeryMetadata"/>, and after <c>UseRateLimiting</c> and <c>UseRequestTimeouts</c> when
/// the application uses them, so a flood is turned away before any body is read and a form read runs
/// under the endpoint's timeout.
/// </para>
/// </remarks>
public static class AntiforgeryWebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the antiforgery middleware, which validates unsafe-method requests to endpoints that carry
        /// <see cref="AntiforgeryMetadata.Required"/> and answers a failed validation with
        /// <c>400 Bad Request</c> as <c>application/problem+json</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Register it after <c>UseRouting</c>. Requests with a safe method (<c>GET</c>, <c>HEAD</c>,
        /// <c>OPTIONS</c>, <c>TRACE</c>) and CORS preflights pass through unvalidated; <c>QUERY</c> carries
        /// a body and is validated. The request token is read from the configured header
        /// (<see cref="HttpAntiforgeryOptions.HeaderName"/>) and, when the request carries none and has a
        /// form body, from the form field (<see cref="HttpAntiforgeryOptions.FormFieldName"/>); reading the
        /// form parses it once for the exchange, so a form-bound endpoint binds from the same parse.
        /// </para>
        /// <para>
        /// An endpoint that requires validation fails with an <see cref="InvalidOperationException"/> when
        /// it is dispatched without this middleware having processed it: the middleware is missing, or
        /// registered ahead of <c>UseRouting</c>.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// When the pipeline is built: antiforgery has not been registered (call
        /// <c>builder.Services.AddAntiforgery</c>).
        /// </exception>
        public IWebApplicationPipelineBuilder UseAntiforgery()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use((application, next) =>
            {
                // Pipeline build, not request time: an application without AddAntiforgery fails to start.
                // The last registration is the one the host seeds onto exchanges, since later features
                // replace earlier ones in the same slot.
                AntiforgeryFeature registration = application.Features.OfType<AntiforgeryFeature>().LastOrDefault()
                    ?? throw new InvalidOperationException(
                        "Antiforgery has not been registered. Call builder.Services.AddAntiforgery() before UseAntiforgery().");

                AntiforgeryMiddleware middleware = new(registration);
                return context => middleware.InvokeAsync(context, next);
            });
        }
    }
}
