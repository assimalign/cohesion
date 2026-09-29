using System;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Creates JSON Web Token validators and evaluates the audience rule profiles leave to their callers.
/// </summary>
public static class JsonWebTokenValidator
{
    /// <summary>
    /// Creates a validator that accepts only ES256 tokens signed by an EC P-256 key the profile trusts for
    /// the token's issuer.
    /// </summary>
    /// <param name="profile">The trust and claim rules to apply.</param>
    /// <returns>An ES256 JSON Web Token validator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The key is selected by the token's <c>kid</c> header from the issuer's
    /// <see cref="JsonWebKeySet"/>; an absent or unknown <c>kid</c>, any algorithm other than
    /// <c>ES256</c>, and an unsecured token are rejected. The ECDSA instance is created from the selected
    /// <see cref="JsonWebKey"/> per validation and disposed before the method returns.
    /// </remarks>
    public static IJsonWebTokenValidator CreateEs256(JsonWebTokenValidationProfile profile)
        => new Es256JsonWebTokenValidator(profile);

    /// <summary>Determines whether a token's <c>aud</c> claim contains an audience.</summary>
    /// <param name="token">The token to inspect.</param>
    /// <param name="audience">The audience to look for.</param>
    /// <returns>
    /// <see langword="true"/> when an <c>aud</c> entry ordinally equals <paramref name="audience"/>;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    public static bool HasAudience(IJsonWebToken token, string? audience)
    {
        ArgumentNullException.ThrowIfNull(token);

        for (int index = 0; index < token.Audiences.Count; index++)
        {
            if (string.Equals(token.Audiences[index], audience, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
