using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken.Internal;

/// <summary>
/// Verifies ES256 compact JSON Web Tokens against a profile's trusted issuer keys and claim rules.
/// </summary>
internal sealed class Es256JsonWebTokenValidator : IJsonWebTokenValidator
{
    public Es256JsonWebTokenValidator(JsonWebTokenValidationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
    }

    /// <inheritdoc />
    public JsonWebTokenValidationProfile Profile { get; }

    /// <inheritdoc />
    public bool TryValidate(string compactToken, DateTimeOffset now, [NotNullWhen(true)] out JsonWebToken? token)
        => Es256JsonWebTokenValidation.TryValidate(Profile, compactToken, now, null, out token);
}
