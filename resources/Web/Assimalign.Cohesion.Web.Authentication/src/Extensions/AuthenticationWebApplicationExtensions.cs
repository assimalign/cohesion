using System;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Pipeline installation of authentication.
/// </summary>
/// <remarks>
/// Registration is <c>builder.Services.AddAuthentication(authentication => authentication.AddCookie())</c>,
/// a component integration over <see cref="AuthenticationBuilder"/> that the application's compilation
/// receives (owner decision 34): the service is registered as an <c>IHttpFeature</c> singleton and
/// schemes are registered as values, so this package takes no service-container, configuration-binding
/// or hosting reference. The scheme verbs themselves (<c>AddCookie</c>, <c>AddJwtBearer</c>) ship with
/// their handler packages and graft onto <see cref="AuthenticationBuilder"/>.
/// </remarks>
public static class AuthenticationWebApplicationExtensions
{
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
