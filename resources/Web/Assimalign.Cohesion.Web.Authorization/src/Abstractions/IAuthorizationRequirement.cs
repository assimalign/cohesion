using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization;

/// <summary>
/// One condition an <see cref="AuthorizationPolicy"/> places on a request. A policy authorizes a
/// request only when every one of its requirements is satisfied.
/// </summary>
/// <remarks>
/// <para>
/// A requirement evaluates itself. There is no handler registry and no service container behind it:
/// the policy holds the requirement by reference and calls <see cref="EvaluateAsync"/>, so nothing is
/// discovered or activated at run time and the model stays NativeAOT-safe. The built-in requirements
/// (an authenticated user, roles, claims with allowed values, and delegate assertions) are created
/// through <see cref="AuthorizationPolicyBuilder"/>. Implement this interface for a condition they
/// cannot express, and add it with <see cref="AuthorizationPolicyBuilder.AddRequirements"/>.
/// </para>
/// <para>
/// A policy is built once and evaluated concurrently for every request it governs, so an
/// implementation must be thread-safe. A policy stops at its first unsatisfied requirement, so a
/// requirement must not depend on being evaluated. The principal under evaluation is the BCL
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> in <see cref="AuthorizationContext.User"/>.
/// </para>
/// </remarks>
public interface IAuthorizationRequirement
{
    /// <summary>
    /// Evaluates the requirement against the principal and exchange in <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The principal being authorized and the exchange it made.</param>
    /// <param name="cancellationToken">A token that cancels the evaluation; the middleware passes the request's.</param>
    /// <returns><see langword="true"/> when the requirement is satisfied; otherwise <see langword="false"/>.</returns>
    ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default);
}
