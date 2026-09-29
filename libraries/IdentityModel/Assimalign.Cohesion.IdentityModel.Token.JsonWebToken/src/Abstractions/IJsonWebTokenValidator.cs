using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

/// <summary>
/// Verifies a compact JSON Web Token's signature against its issuer's trusted keys and validates its
/// document and profile rules.
/// </summary>
/// <remarks>
/// Unlike <see cref="JsonWebToken.Validate(JsonWebTokenValidationOptions)"/>, a successful validation
/// here means the signature was verified with a key the profile trusts for the token's issuer. Every
/// failure — malformed input, an untrusted issuer or key, a bad signature, or a violated claim rule —
/// yields the same <see langword="false"/> result, so a caller cannot be used as an oracle for which
/// rule failed.
/// </remarks>
public interface IJsonWebTokenValidator
{
    /// <summary>Gets the rules this validator applies.</summary>
    JsonWebTokenValidationProfile Profile { get; }

    /// <summary>Verifies and validates a compact JSON Web Token.</summary>
    /// <param name="compactToken">The compact JWS serialization.</param>
    /// <param name="now">The instant temporal rules are evaluated at.</param>
    /// <param name="token">When this method returns <see langword="true"/>, the verified token.</param>
    /// <returns>
    /// <see langword="true"/> when the token is signed by a key trusted for its issuer and satisfies every
    /// profile rule; otherwise, <see langword="false"/>.
    /// </returns>
    bool TryValidate(string compactToken, DateTimeOffset now, [NotNullWhen(true)] out JsonWebToken? token);
}
