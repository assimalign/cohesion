using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// An <see cref="IAuthenticationHandler"/> that can also establish and clear an authentication
/// session — the cookie handler is the canonical example. Bearer-style handlers, which validate
/// a caller-supplied token on every request and hold no session, deliberately do not implement
/// this interface, so a <c>SignInAsync</c> against a bearer scheme fails fast rather than
/// silently no-op'ing.
/// </summary>
public interface IAuthenticationSignInHandler : IAuthenticationHandler
{
    /// <summary>
    /// Establishes an authentication session for <paramref name="user"/> (for the cookie handler,
    /// issues a protected ticket cookie).
    /// </summary>
    /// <param name="user">The principal to sign in.</param>
    /// <param name="properties">Optional properties (persistence, expiry, redirect).</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the sign-in has been written to the response.</returns>
    Task SignInAsync(ClaimsPrincipal user, AuthenticationProperties? properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the authentication session (for the cookie handler, deletes the ticket cookie).
    /// </summary>
    /// <param name="properties">Optional properties (for example a post-sign-out redirect target).</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the sign-out has been written to the response.</returns>
    Task SignOutAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default);
}
