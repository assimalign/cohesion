using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using Assimalign.Cohesion.Web.Authorization.Internal;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// Pipeline installation of, and read access to the registered options for, authorization.
/// </summary>
/// <remarks>
/// <para>
/// Registration is <c>builder.Services.AddAuthorization(...)</c>, a component integration the
/// application's compilation receives (<see cref="AuthorizationComponents"/>, owner decision 34). It
/// captures the policies as values and registers them as an application feature, an
/// <see cref="Http.IHttpFeature"/> singleton the host hands to the pipeline. This package takes no
/// dependency-injection or hosting reference; a composition surface without a service container
/// registers the same feature through <c>IWebApplicationBuilder.AddFeature</c>.
/// </para>
/// <para>
/// The supported order is <c>UseRouting</c> → <c>UseAuthentication</c> → <c>UseAuthorization</c> →
/// policy middleware that serves or caches responses (<c>UseOutputCache</c>) → endpoint. A CORS
/// middleware belongs ahead of <c>UseAuthorization</c>, which never authorizes a preflight.
/// </para>
/// <para>
/// <c>TryGetAuthorizationOptions</c> reads the registration back from the application context, the way
/// <c>UseAuthorization</c> does, for components that describe the application's endpoints rather than
/// serve them.
/// </para>
/// </remarks>
public static class AuthorizationWebApplicationExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Adds the authorization middleware, which authorizes each request against its endpoint's
        /// <see cref="AuthorizationMetadata"/> (or the fallback policy) and challenges or forbids the
        /// requests that fail.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Register it after <c>UseRouting</c>, which publishes the endpoint, and after
        /// <c>UseAuthentication</c>, which establishes <c>context.User</c> for policies that name no
        /// authentication schemes. A failing request is answered through Web.Authentication: a challenge
        /// when no authenticated principal is present, a forbid otherwise, through the policy's schemes
        /// or, when it names none, the default challenge and forbid schemes. The endpoint does not run.
        /// </para>
        /// <para>
        /// An endpoint whose authorization metadata requires authorization fails with an
        /// <see cref="InvalidOperationException"/> at dispatch when this middleware did not process it:
        /// when it is missing, or registered ahead of <c>UseRouting</c> where no endpoint is known yet.
        /// </para>
        /// </remarks>
        /// <returns>The same pipeline builder, for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the pipeline is composed (at application start) if <c>builder.Services.AddAuthorization</c> was
        /// not called.
        /// </exception>
        public IWebApplicationPipelineBuilder UseAuthorization()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use((application, next) =>
            {
                // Resolved once, when the pipeline is composed: a missing registration fails startup
                // rather than a request. Read through the public accessor, so the middleware and every
                // component that describes the application see the same registration.
                if (!application.TryGetAuthorizationOptions(out AuthorizationOptions? options))
                {
                    throw new InvalidOperationException(
                        "Authorization has not been registered. Call builder.Services.AddAuthorization() before " +
                        "UseAuthorization().");
                }

                AuthorizationMiddleware middleware = new(options);

                return context => middleware.InvokeAsync(context, next);
            });
        }
    }

    extension(IWebApplicationContext context)
    {
        /// <summary>
        /// Gets the authorization options the application registered with <c>AddAuthorization</c>: the
        /// default, fallback and named policies <c>UseAuthorization</c> evaluates, and through each policy
        /// its authentication schemes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The result is the instance <c>AddAuthorization</c> captured, which is read-only: every mutator
        /// throws <see cref="InvalidOperationException"/>, so a reader cannot change what the middleware
        /// enforces, and reads are safe from any thread. It is resolved the way <c>UseAuthorization</c>
        /// resolves it (the last registration wins), so a component that describes the application's
        /// endpoints, such as an OpenAPI document generator, sees the policies the middleware applies.
        /// Combine them with an endpoint's metadata through <see cref="AuthorizationOptions.GetEffectivePolicy"/>
        /// rather than re-deriving the combination rules.
        /// </para>
        /// <para>
        /// The lookup scans the application's registered features with type tests; nothing is discovered
        /// by reflection, so it is safe under NativeAOT and trimming.
        /// </para>
        /// </remarks>
        /// <param name="options">
        /// The registered options, when the application called <c>AddAuthorization</c>; otherwise
        /// <see langword="null"/>.
        /// </param>
        /// <returns><see langword="true"/> when the application registered authorization.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        public bool TryGetAuthorizationOptions([NotNullWhen(true)] out AuthorizationOptions? options)
        {
            ArgumentNullException.ThrowIfNull(context);

            // The last registration wins, as it does for the per-request feature slot.
            options = context.Features.OfType<AuthorizationFeature>().LastOrDefault()?.Options;

            return options is not null;
        }
    }
}
