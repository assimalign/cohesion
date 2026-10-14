using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Requires an authenticated user: a principal with at least one authenticated identity. This is
/// the requirement behind <see cref="AuthorizationPolicyBuilder.RequireAuthenticatedUser"/> and the
/// default policy.
/// </summary>
internal sealed class DenyAnonymousRequirement : IAuthorizationRequirement
{
    private DenyAnonymousRequirement()
    {
    }

    /// <summary>
    /// Gets the shared instance. The requirement carries no state.
    /// </summary>
    public static DenyAnonymousRequirement Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new ValueTask<bool>(IsAuthenticated(context.User));
    }

    /// <summary>
    /// Determines whether <paramref name="user"/> carries at least one authenticated identity. A
    /// principal combined from several schemes is authenticated when any of them authenticated it.
    /// </summary>
    /// <param name="user">The principal.</param>
    /// <returns><see langword="true"/> when an identity is authenticated.</returns>
    public static bool IsAuthenticated(ClaimsPrincipal user)
    {
        foreach (ClaimsIdentity identity in user.Identities)
        {
            if (identity.IsAuthenticated)
            {
                return true;
            }
        }

        return false;
    }
}
