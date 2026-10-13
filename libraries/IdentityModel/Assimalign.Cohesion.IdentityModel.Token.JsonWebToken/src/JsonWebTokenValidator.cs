using System;
using System.Diagnostics.CodeAnalysis;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

// Deviates from the internal-implementation rule per owner decision 59 (#1465): the implementation is public.
/// <summary>
/// Verifies ES256 compact JSON Web Tokens against a profile's trusted issuer keys and claim rules.
/// </summary>
public sealed class JsonWebTokenValidator : IJsonWebTokenValidator
{
    private JsonWebTokenValidator(JsonWebTokenValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
    }

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
    public static JsonWebTokenValidator CreateEs256(JsonWebTokenValidationProfile profile)
        => new JsonWebTokenValidator(profile);

    /// <inheritdoc />
    public JsonWebTokenValidationProfile Profile { get; }

    /// <inheritdoc />
    public bool TryValidate(string compactToken, DateTimeOffset now, [NotNullWhen(true)] out JsonWebToken? token)
        => Es256JsonWebTokenValidation.TryValidate(Profile, compactToken, now, null, out token);
}
