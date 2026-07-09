using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// A per-request handler for a single <see cref="AuthenticationScheme"/>. The handler owns the
/// scheme's wire behavior: reading and validating a credential (<see cref="AuthenticateAsync"/>),
/// prompting for one (<see cref="ChallengeAsync"/>), and denying an authenticated-but-unauthorized
/// request (<see cref="ForbidAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// A handler is created fresh per request by <see cref="AuthenticationScheme.CreateHandler"/> and
/// bound to its scheme and context by <see cref="InitializeAsync"/> before any verb runs. It may
/// therefore cache per-request state (for example the result of a single
/// <see cref="AuthenticateAsync"/> so a later <see cref="ChallengeAsync"/> can reference it).
/// </para>
/// <para>
/// Schemes that additionally support establishing and clearing a session (the cookie handler)
/// implement <see cref="IAuthenticationSignInHandler"/>.
/// </para>
/// </remarks>
public interface IAuthenticationHandler
{
    /// <summary>
    /// Binds the handler to its scheme and the current request. Called once before any verb.
    /// </summary>
    /// <param name="scheme">The scheme this handler serves.</param>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when initialization is done.</returns>
    Task InitializeAsync(AuthenticationScheme scheme, IHttpContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates the request's credential for this scheme.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>
    /// A success result carrying the authenticated ticket, a no-result outcome when no credential
    /// was present, or a failure when a credential was present but rejected.
    /// </returns>
    Task<AuthenticateResult> AuthenticateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Challenges the request: prompts the caller to authenticate. For an interactive endpoint
    /// this is typically a redirect to a login page; for an API endpoint it is a bare
    /// <c>401 Unauthorized</c> with a scheme-appropriate <c>WWW-Authenticate</c> header.
    /// </summary>
    /// <param name="properties">Optional properties (for example a post-login redirect target).</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the challenge has been written to the response.</returns>
    Task ChallengeAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forbids the request: the caller is authenticated but not permitted. For an interactive
    /// endpoint this is typically a redirect to an access-denied page; for an API endpoint it is
    /// a bare <c>403 Forbidden</c>.
    /// </summary>
    /// <param name="properties">Optional properties (for example an access-denied redirect target).</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    Task ForbidAsync(AuthenticationProperties? properties, CancellationToken cancellationToken = default);
}
