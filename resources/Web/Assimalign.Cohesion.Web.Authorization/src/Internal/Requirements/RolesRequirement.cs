using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Web.Authorization.Internal;

/// <summary>
/// Requires the principal to be in at least one of a set of roles, decided by
/// <see cref="ClaimsPrincipal.IsInRole(string)"/> (so each identity's own role claim type applies).
/// This is the requirement behind <see cref="AuthorizationPolicyBuilder.RequireRole(string[])"/> and
/// <see cref="AuthorizationMetadata.Roles"/>.
/// </summary>
internal sealed class RolesRequirement : IAuthorizationRequirement
{
    private readonly string[] _roles;

    /// <summary>
    /// Creates the requirement.
    /// </summary>
    /// <param name="roles">The accepted roles. At least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roles"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="roles"/> is empty, or contains a <see langword="null"/>, empty, or whitespace entry.
    /// </exception>
    public RolesRequirement(IEnumerable<string> roles)
    {
        _roles = AuthorizationNames.Copy(roles, nameof(roles), "role");

        if (_roles.Length == 0)
        {
            throw new ArgumentException(
                "At least one role is required; a role requirement with none could never be satisfied.",
                nameof(roles));
        }
    }

    /// <summary>
    /// Gets the accepted roles.
    /// </summary>
    public IReadOnlyList<string> Roles => _roles;

    /// <inheritdoc />
    public ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        ClaimsPrincipal user = context.User;

        foreach (string role in _roles)
        {
            if (user.IsInRole(role))
            {
                return new ValueTask<bool>(true);
            }
        }

        return new ValueTask<bool>(false);
    }
}
