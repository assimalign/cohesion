using System;
using System.Security.Claims;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// Default <see cref="IHttpAuthenticationFeature"/> implementation — a
/// typed holder for the resolved <see cref="ClaimsPrincipal"/>.
/// Authentication middleware constructs this with the principal it
/// resolved and attaches it through the context's feature collection.
/// </summary>
internal sealed class HttpAuthenticationFeature : IHttpAuthenticationFeature
{
    public HttpAuthenticationFeature(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        User = user;
    }

    /// <inheritdoc />
    public ClaimsPrincipal User { get; set; }
}
