using System;

namespace Assimalign.Cohesion.Web.Authentication;

/// <summary>
/// The public entry point that builds an <see cref="IAuthenticationService"/> over an
/// <see cref="AuthenticationOptions"/> registry. Create one at composition time (a
/// <c>*.Hosting</c> project) and install it as a feature on every request.
/// </summary>
/// <remarks>
/// The returned service reads <paramref name="options"/> live, so a composition root can create
/// the service first and register schemes onto the same options afterward (the pattern used when
/// <c>AddAuthentication</c> installs the service feature eagerly and the chained
/// <c>AddCookie</c>/<c>AddJwtBearer</c> calls add schemes).
/// </remarks>
public static class AuthenticationService
{
    /// <summary>
    /// Creates an authentication service over <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The scheme registry and default-scheme selections.</param>
    /// <returns>A shareable authentication service that is also an <see cref="Http.IHttpFeature"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static IAuthenticationService Create(AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DefaultAuthenticationService(options);
    }
}
