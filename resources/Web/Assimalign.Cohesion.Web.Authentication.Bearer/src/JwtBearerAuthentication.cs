using System;

using Assimalign.Cohesion.Web.Authentication.Bearer.Internal;

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
    /// <exception cref="InvalidOperationException">
    /// <see cref="JwtBearerOptions.RequireSignedTokens"/> is <see langword="true"/> but no signing keys
    /// are configured; or <see cref="JwtBearerOptions.ValidateIssuer"/> is <see langword="true"/> but
    /// <see cref="JwtBearerOptions.ValidIssuers"/> is empty; or
    /// <see cref="JwtBearerOptions.ValidateAudience"/> is <see langword="true"/> but
    /// <see cref="JwtBearerOptions.ValidAudiences"/> is empty.
    /// </exception>
    public static IAuthenticationHandler CreateHandler(JwtBearerOptions options)
    {
        Validate(options);
        return new JwtBearerHandler(options);
    }

    /// <summary>
    /// Validates bearer options against the fail-closed defaults: a signed-token scheme needs a
    /// signing key, and issuer and audience validation need at least one accepted value each
    /// unless the application opted out explicitly.
    /// </summary>
    /// <param name="options">The configured bearer options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A required value is missing; the message names the property and the opt-out.</exception>
    internal static void Validate(JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RequireSignedTokens && options.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one signing key when RequireSignedTokens is true.");
        }

        if (options.ValidateIssuer && options.ValidIssuers.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one entry in ValidIssuers while ValidateIssuer is true. " +
                "Add the issuer (the token's 'iss') the scheme accepts, or set ValidateIssuer = false to accept any issuer a signing key verifies.");
        }

        if (options.ValidateAudience && options.ValidAudiences.Count == 0)
        {
            throw new InvalidOperationException(
                "JwtBearerOptions requires at least one entry in ValidAudiences while ValidateAudience is true. " +
                "Add the audience (the token's 'aud') this service answers to, or set ValidateAudience = false to accept tokens minted for any audience.");
        }
    }
}
