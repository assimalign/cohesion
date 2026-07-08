using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The orchestrator that dispatches the authentication verbs to the right scheme handler,
/// applying default-scheme selection when a caller does not name a scheme. It is the single
/// object <c>context.AuthenticateAsync()</c> and the authentication middleware talk to.
/// </summary>
/// <remarks>
/// <para>
/// The service is a builder-time singleton installed on every request's
/// <see cref="IHttpContext.Features"/> collection (hence it is an <see cref="IHttpFeature"/>).
/// Request code reaches it type-keyed through the feature collection — never through request-time
/// service location — which keeps the dispatch reflection- and container-free.
/// </para>
/// <para>
/// The service caches each request's handler and authenticate result on the context, so a
/// challenge that follows an authenticate reuses the same handler instance and a repeated
/// authenticate for the same scheme does not re-parse the credential.
/// </para>
/// </remarks>
public interface IAuthenticationService : IHttpFeature
{
    /// <summary>
    /// Gets the effective default authenticate scheme, or <see langword="null"/> when none is
    /// configured. The authentication middleware reads this to decide whether to auto-authenticate
    /// the request and populate <c>context.User</c>.
    /// </summary>
    string? DefaultAuthenticateScheme { get; }

    /// <summary>
    /// Authenticates the current request against a scheme.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scheme">The scheme name, or <see langword="null"/> to use the default authenticate scheme.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The authentication result.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">No scheme was named and no default authenticate scheme is configured, or the named scheme is not registered.</exception>
    Task<AuthenticateResult> AuthenticateAsync(IHttpContext context, string? scheme, CancellationToken cancellationToken = default);

    /// <summary>
    /// Challenges the current request against a scheme.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scheme">The scheme name, or <see langword="null"/> to use the default challenge scheme.</param>
    /// <param name="properties">Optional challenge properties.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the challenge has been written.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">No scheme was named and no default challenge scheme is configured, or the named scheme is not registered.</exception>
    Task ChallengeAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forbids the current request against a scheme.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scheme">The scheme name, or <see langword="null"/> to use the default forbid scheme.</param>
    /// <param name="properties">Optional forbid properties.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">No scheme was named and no default forbid scheme is configured, or the named scheme is not registered.</exception>
    Task ForbidAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs a principal in against a scheme.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scheme">The scheme name, or <see langword="null"/> to use the default sign-in scheme.</param>
    /// <param name="user">The principal to sign in.</param>
    /// <param name="properties">Optional sign-in properties.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the sign-in has been written.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> or <paramref name="user"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The resolved scheme is not registered or its handler does not support sign-in.</exception>
    Task SignInAsync(IHttpContext context, string? scheme, ClaimsPrincipal user, AuthenticationProperties? properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signs out of a scheme.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="scheme">The scheme name, or <see langword="null"/> to use the default sign-out scheme.</param>
    /// <param name="properties">Optional sign-out properties.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the sign-out has been written.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.InvalidOperationException">The resolved scheme is not registered or its handler does not support sign-out.</exception>
    Task SignOutAsync(IHttpContext context, string? scheme, AuthenticationProperties? properties, CancellationToken cancellationToken = default);
}
