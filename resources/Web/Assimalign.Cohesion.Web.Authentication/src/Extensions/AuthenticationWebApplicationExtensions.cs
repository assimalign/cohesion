using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Builder-time registration and pipeline installation for authentication. Registration is
/// dependency-free: the service is attached to the application as a typed feature and schemes are
/// registered as values — no service container, configuration binding, or hosting reference is
/// involved, so any composition surface that implements <see cref="IWebApplicationBuilder"/> can
/// wire authentication.
/// </summary>
/// <remarks>
/// These verbs moved here from <c>Assimalign.Cohesion.Web.Hosting</c> when the Web area adopted the
/// rule that the hosting/runtime module neither references nor is referenced by the feature
/// libraries; the scheme verbs themselves (<c>AddCookie</c>, <c>AddJwtBearer</c>) ship with their
/// handler packages and graft onto <see cref="AuthenticationBuilder"/>.
/// </remarks>
public static class AuthenticationWebApplicationExtensions
{
    extension(IWebApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the authentication services and returns a builder for registering schemes.
        /// </summary>
        /// <param name="configure">An optional callback to configure default-scheme selection.</param>
        /// <param name="dataProtectionProvider">
        /// An optional data-protection provider used to seal authentication tickets. When omitted, a
        /// file-system-backed rotating key ring under <see cref="AppContext.BaseDirectory"/> is
        /// created on first use.
        /// </param>
        /// <returns>An <see cref="AuthenticationBuilder"/> for chaining scheme registrations.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public AuthenticationBuilder AddAuthentication(
            Action<AuthenticationOptions>? configure = null,
            IDataProtectionProvider? dataProtectionProvider = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            AuthenticationOptions options = new();
            configure?.Invoke(options);

            // Register the service (an IHttpFeature) as a builder-time singleton. It reads the
            // options live, so schemes registered by chained verbs that run after this call are
            // still resolved at request time.
            IAuthenticationService service = AuthenticationService.Create(options);
            builder.AddFeature(service);

            return new AuthenticationBuilder(options, dataProtectionProvider);
        }

        /// <summary>
        /// Adds the authentication services with a default scheme and returns a builder for
        /// registering schemes.
        /// </summary>
        /// <param name="defaultScheme">The default scheme applied to every verb that does not set its own default.</param>
        /// <param name="configure">An optional callback to further configure default-scheme selection.</param>
        /// <returns>An <see cref="AuthenticationBuilder"/> for chaining scheme registrations.</returns>
        /// <exception cref="ArgumentException"><paramref name="defaultScheme"/> is <see langword="null"/> or whitespace.</exception>
        public AuthenticationBuilder AddAuthentication(string defaultScheme, Action<AuthenticationOptions>? configure = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(defaultScheme);

            return builder.AddAuthentication(options =>
            {
                options.DefaultScheme = defaultScheme;
                configure?.Invoke(options);
            });
        }
    }

    extension(IWebApplicationPipelineBuilder pipeline)
    {
        /// <summary>
        /// Adds the authentication middleware, which authenticates each request against the default
        /// authenticate scheme and populates <c>context.User</c>. When no default authenticate
        /// scheme is configured, the middleware is a pass-through and authentication happens only on
        /// demand via <c>context.AuthenticateAsync</c>.
        /// </summary>
        /// <returns>The pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="pipeline"/> is <see langword="null"/>.</exception>
        public IWebApplicationPipelineBuilder UseAuthentication()
        {
            ArgumentNullException.ThrowIfNull(pipeline);

            pipeline.Use(async (context, next) =>
            {
                if (context.Features.Get<IAuthenticationService>() is { DefaultAuthenticateScheme: { } scheme } service)
                {
                    AuthenticateResult result = await service
                        .AuthenticateAsync(context, scheme, context.RequestCancelled)
                        .ConfigureAwait(false);

                    if (result.Succeeded && result.Principal is not null)
                    {
                        context.User = result.Principal;
                    }
                }

                await next.Invoke(context).ConfigureAwait(false);
            });

            return pipeline;
        }
    }
}
