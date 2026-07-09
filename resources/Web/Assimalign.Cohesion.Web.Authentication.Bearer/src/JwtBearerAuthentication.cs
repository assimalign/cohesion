using System;

namespace Assimalign.Cohesion.Web.Authentication.Bearer;

/// <summary>
/// Factory for the JWT bearer authentication handler. The composition root (a <c>*.Hosting</c>
/// project) calls <see cref="CreateHandler(JwtBearerOptions)"/> from a scheme's handler factory;
/// the concrete handler stays internal so <see cref="IAuthenticationHandler"/> remains the only
/// public surface.
/// </summary>
public static class JwtBearerAuthentication
{
    /// <summary>
    /// Creates a JWT bearer authentication handler over the supplied options.
    /// </summary>
    /// <param name="options">The configured bearer options.</param>
    /// <returns>A bearer handler as an <see cref="IAuthenticationHandler"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="JwtBearerOptions.RequireSignedTokens"/> is <see langword="true"/> but no signing keys are configured.</exception>
    public static IAuthenticationHandler CreateHandler(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RequireSignedTokens && options.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one signing key when RequireSignedTokens is true.");
        }

        return new JwtBearerHandler(options);
    }
}
