using System;

namespace Assimalign.Cohesion.IdentityModel.Token;

/// <summary>
/// Provides convenience accessors over <see cref="IIdentityToken" />. These are
/// <c>extension(...)</c> members rather than interface members so the token data contract
/// stays minimal and the convenience surface can grow without breaking implementers.
/// </summary>
public static class IdentityTokenExtensions
{
    extension(IIdentityToken token)
    {
        /// <summary>
        /// Determines whether the token is intended for the provided audience, comparing
        /// ordinally.
        /// </summary>
        /// <param name="audience">The audience value to match.</param>
        /// <returns><see langword="true" /> when the audience is present; otherwise <see langword="false" />.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="audience" /> is null or whitespace.</exception>
        public bool HasAudience(string audience)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audience);

            var audiences = token.Audiences;
            for (var index = 0; index < audiences.Count; index++)
            {
                if (string.Equals(audiences[index], audience, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether the token's primary validity window is active at the provided
        /// instant, allowing the provided clock skew. A token with no temporal bounds is
        /// always active. The skew is applied to <paramref name="at" /> so extreme wire
        /// timestamps never overflow.
        /// </summary>
        /// <param name="at">The instant to evaluate.</param>
        /// <param name="clockSkew">The allowed clock skew. Defaults to none.</param>
        /// <returns><see langword="true" /> when the token is active; otherwise <see langword="false" />.</returns>
        public bool IsActive(DateTimeOffset at, TimeSpan clockSkew = default)
        {
            if (token.NotBefore is { } notBefore && at + clockSkew < notBefore)
            {
                return false;
            }

            if (token.ExpiresAt is { } expiresAt && at - clockSkew >= expiresAt)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the token is expired at the provided instant, allowing the
        /// provided clock skew. A token with no expiry is never expired.
        /// </summary>
        /// <param name="at">The instant to evaluate.</param>
        /// <param name="clockSkew">The allowed clock skew. Defaults to none.</param>
        /// <returns><see langword="true" /> when the token is expired; otherwise <see langword="false" />.</returns>
        public bool IsExpired(DateTimeOffset at, TimeSpan clockSkew = default)
            => token.ExpiresAt is { } expiresAt && at - clockSkew >= expiresAt;
    }
}
