using System;
using System.Security.Claims;

namespace Assimalign.Cohesion.Web.Authentication.Internal;

/// <summary>
/// Default <see cref="IAuthenticationFeature"/> implementation — a
/// typed holder for the resolved <see cref="ClaimsPrincipal"/>.
/// Authentication middleware constructs this with the principal it
/// resolved and attaches it through the context's feature collection.
/// </summary>
internal sealed class AuthenticationFeature : IAuthenticationFeature
{
    public AuthenticationFeature(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        User = user;
    }

    /// <inheritdoc />
    public string Name => nameof(AuthenticationFeature);

    /// <inheritdoc />
    public ClaimsPrincipal User { get; set; }
}
