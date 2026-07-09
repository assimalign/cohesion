using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Surfaces the authentication verbs (<c>AuthenticateAsync</c>, <c>ChallengeAsync</c>,
/// <c>ForbidAsync</c>, <c>SignInAsync</c>, <c>SignOutAsync</c>) on <see cref="IHttpContext"/>,
/// dispatching through the <see cref="IAuthenticationService"/> installed on the context's
/// feature collection by the authentication middleware.
/// </summary>
/// <remarks>
/// These mirror the familiar ASP.NET Core <c>HttpContext.AuthenticateAsync()</c> surface but
/// resolve the service from the strongly-typed feature collection rather than a request-time
/// service container, keeping dispatch reflection- and container-free. Each throws
/// <see cref="InvalidOperationException"/> when authentication has not been configured (no service
/// feature is present), which is the signal that <c>AddAuthentication</c>/<c>UseAuthentication</c>
/// were not wired at the composition root.
/// </remarks>
public static class HttpContextAuthenticationVerbExtensions
{
    extension(IHttpContext context)
    {
        /// <summary>
        /// Authenticates the current request against a scheme.
        /// </summary>
        /// <param name="scheme">The scheme name, or <see langword="null"/> for the default authenticate scheme.</param>
        /// <param name="cancellationToken">A token to observe for cancellation.</param>
        /// <returns>The authentication result.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Authentication has not been configured for this request.</exception>
        public Task<AuthenticateResult> AuthenticateAsync(string? scheme = null, CancellationToken cancellationToken = default)
            => ResolveService(context).AuthenticateAsync(context, scheme, cancellationToken);

        /// <summary>
        /// Challenges the current request against a scheme.
        /// </summary>
        /// <param name="scheme">The scheme name, or <see langword="null"/> for the default challenge scheme.</param>
        /// <param name="properties">Optional challenge properties.</param>
        /// <param name="cancellationToken">A token to observe for cancellation.</param>
        /// <returns>A task that completes when the challenge has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Authentication has not been configured for this request.</exception>
        public Task ChallengeAsync(string? scheme = null, AuthenticationProperties? properties = null, CancellationToken cancellationToken = default)
            => ResolveService(context).ChallengeAsync(context, scheme, properties, cancellationToken);

        /// <summary>
        /// Forbids the current request against a scheme.
        /// </summary>
        /// <param name="scheme">The scheme name, or <see langword="null"/> for the default forbid scheme.</param>
        /// <param name="properties">Optional forbid properties.</param>
        /// <param name="cancellationToken">A token to observe for cancellation.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Authentication has not been configured for this request.</exception>
        public Task ForbidAsync(string? scheme = null, AuthenticationProperties? properties = null, CancellationToken cancellationToken = default)
            => ResolveService(context).ForbidAsync(context, scheme, properties, cancellationToken);

        /// <summary>
        /// Signs a principal in against a scheme.
        /// </summary>
        /// <param name="user">The principal to sign in.</param>
        /// <param name="scheme">The scheme name, or <see langword="null"/> for the default sign-in scheme.</param>
        /// <param name="properties">Optional sign-in properties.</param>
        /// <param name="cancellationToken">A token to observe for cancellation.</param>
        /// <returns>A task that completes when the sign-in has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="user"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Authentication has not been configured for this request, or the scheme does not support sign-in.</exception>
        public Task SignInAsync(ClaimsPrincipal user, string? scheme = null, AuthenticationProperties? properties = null, CancellationToken cancellationToken = default)
            => ResolveService(context).SignInAsync(context, scheme, user, properties, cancellationToken);

        /// <summary>
        /// Signs out of a scheme.
        /// </summary>
        /// <param name="scheme">The scheme name, or <see langword="null"/> for the default sign-out scheme.</param>
        /// <param name="properties">Optional sign-out properties.</param>
        /// <param name="cancellationToken">A token to observe for cancellation.</param>
        /// <returns>A task that completes when the sign-out has been written.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">Authentication has not been configured for this request, or the scheme does not support sign-out.</exception>
        public Task SignOutAsync(string? scheme = null, AuthenticationProperties? properties = null, CancellationToken cancellationToken = default)
            => ResolveService(context).SignOutAsync(context, scheme, properties, cancellationToken);
    }

    private static IAuthenticationService ResolveService(IHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Features.Get<IAuthenticationService>()
            ?? throw new InvalidOperationException(
                "Authentication has not been configured for this request. Call AddAuthentication at the " +
                "composition root and UseAuthentication in the request pipeline.");
    }
}
