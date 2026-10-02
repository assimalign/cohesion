using System;
using System.Linq;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Antiforgery.Internal;

namespace Assimalign.Cohesion.Web.Antiforgery;

/// <summary>
/// Builder-time registration and pipeline installation for antiforgery (CSRF) protection.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddAntiforgery</c> creates the application's antiforgery service once, over the
/// <c>Assimalign.Cohesion.Http.Antiforgery</c> signed double-submit engine, and registers it as an
/// application feature: every exchange carries it, so handlers mint tokens with
/// <c>context.RequireAntiforgery.GetAndStoreTokens(context)</c>. <c>UseAntiforgery</c> adds the middleware
/// that validates protected endpoints. Composition is dependency-free: the service is a value attached
/// as a typed feature, and no service container, configuration binding, or request-time service location
/// is involved.
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
    // The purpose chain the data-protection protector is derived for. It is part of the token format:
    // changing it invalidates every outstanding token, so a new chain needs a new version segment.
    private const string purpose = "Assimalign.Cohesion.Web.Antiforgery";
    private const string purposeVersion = "v1";

    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the application's antiforgery service with tokens protected by a per-process random
        /// key. Use this form for development only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without a data-protection provider, tokens are signed with a random key generated when the
        /// application starts: tokens minted before a restart stop validating, and instances behind a load
        /// balancer reject each other's tokens. For any deployed application, use the overload that takes
        /// an <see cref="IDataProtectionProvider"/>, or set <see cref="HttpAntiforgeryOptions.Protector"/>
        /// in <paramref name="configure"/>.
        /// </para>
        /// <para>
        /// Registering again replaces the earlier registration: the last <c>AddAntiforgery</c> call
        /// supplies the service every exchange carries and <c>UseAntiforgery</c> validates with.
        /// </para>
        /// </remarks>
        /// <param name="configure">An optional callback to configure the token names, cookie attributes, and protector.</param>
        /// <returns>The web application builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public IWebApplicationBuilder AddAntiforgery(Action<HttpAntiforgeryOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            return Register(builder, dataProtectionProvider: null, configure);
        }

        /// <summary>
        /// Registers the application's antiforgery service with tokens protected by the application's
        /// data-protection key ring, so they survive restarts and validate on every instance that shares
        /// the key repository.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The service protects tokens with a protector derived from <paramref name="dataProtectionProvider"/>
        /// for the antiforgery purpose, so no other payload the same key ring protects (an authentication
        /// ticket, for example) is accepted as a token. To share the key ring cookie authentication uses,
        /// pass the same provider to both, for example <c>AuthenticationBuilder.DataProtectionProvider</c>.
        /// A protector set explicitly in <paramref name="configure"/> takes precedence over the provider.
        /// </para>
        /// <para>
        /// Registering again replaces the earlier registration: the last <c>AddAntiforgery</c> call
        /// supplies the service every exchange carries and <c>UseAntiforgery</c> validates with.
        /// </para>
        /// </remarks>
        /// <param name="dataProtectionProvider">The application's data-protection provider.</param>
        /// <param name="configure">An optional callback to configure the token names, cookie attributes, and protector.</param>
        /// <returns>The web application builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="builder"/> or <paramref name="dataProtectionProvider"/> is <see langword="null"/>.
        /// </exception>
        public IWebApplicationBuilder AddAntiforgery(
            IDataProtectionProvider dataProtectionProvider,
            Action<HttpAntiforgeryOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(dataProtectionProvider);

            return Register(builder, dataProtectionProvider, configure);
        }
    }

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
        /// When the pipeline is built: antiforgery has not been registered (call <c>AddAntiforgery</c> on
        /// the web application builder).
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
                        "Antiforgery has not been registered. Call AddAntiforgery() on the web application builder before UseAntiforgery().");

                AntiforgeryMiddleware middleware = new(registration);
                return context => middleware.InvokeAsync(context, next);
            });
        }
    }

    private static IWebApplicationBuilder Register(
        IWebApplicationBuilder builder,
        IDataProtectionProvider? dataProtectionProvider,
        Action<HttpAntiforgeryOptions>? configure)
    {
        HttpAntiforgeryOptions options = new();
        configure?.Invoke(options);

        // An explicitly configured protector wins. Otherwise the application's key ring protects tokens,
        // and only without one does the engine fall back to its per-process random key.
        if (options.Protector is null && dataProtectionProvider is not null)
        {
            options.Protector = new DataProtectionAntiforgeryProtector(
                dataProtectionProvider.CreateProtector(purpose, purposeVersion));
        }

        IHttpAntiforgery antiforgery = HttpAntiforgery.Create(options);
        return builder.AddFeature(new AntiforgeryFeature(antiforgery, options));
    }
}
