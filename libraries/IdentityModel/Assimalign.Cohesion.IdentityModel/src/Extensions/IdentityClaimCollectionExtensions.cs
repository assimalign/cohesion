using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Convenience accessors for <see cref="IIdentityClaimCollection" />.
/// </summary>
public static class IdentityClaimCollectionExtensions
{
    extension(IIdentityClaimCollection claims)
    {
        /// <summary>
        /// Gets the string content of the first claim of the provided type, or null when no
        /// such claim exists or its value is not a string.
        /// </summary>
        /// <param name="claimType">The canonical claim type to match.</param>
        /// <returns>The string content, or null.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
        public string? GetString(string claimType)
        {
            return claims.TryGet(claimType, out var claim) && claim.Value.TryGetString(out var value)
                ? value
                : null;
        }

        /// <summary>
        /// Attempts to get the string content of the first claim of the provided type.
        /// </summary>
        /// <param name="claimType">The canonical claim type to match.</param>
        /// <param name="value">When this method returns, contains the string content, if present.</param>
        /// <returns><see langword="true" /> when a string-valued claim exists; otherwise <see langword="false" />.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
        public bool TryGetString(string claimType, [NotNullWhen(true)] out string? value)
        {
            if (claims.TryGet(claimType, out var claim) && claim.Value.TryGetString(out value))
            {
                return true;
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Canonicalizes the claims with the default cross-protocol mappings (see
        /// <see cref="IdentityClaimMappings.Default" />): strictly equivalent wire names are
        /// re-typed to their canonical claim type with values byte-identical and provenance
        /// preserved; everything else passes through unchanged.
        /// </summary>
        /// <returns>The canonicalized collection, or the collection itself when nothing mapped.</returns>
        public IIdentityClaimCollection Canonicalize() => IdentityClaimMapper.Default.Canonicalize(claims);

        /// <summary>
        /// Canonicalizes the claims with the provided mapper.
        /// </summary>
        /// <param name="mapper">The mapper carrying the resolved mappings to apply.</param>
        /// <returns>The canonicalized collection, or the collection itself when nothing mapped.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="mapper" /> is null.</exception>
        public IIdentityClaimCollection Canonicalize(IdentityClaimMapper mapper)
        {
            ArgumentNullException.ThrowIfNull(mapper);
            return mapper.Canonicalize(claims);
        }

        /// <summary>
        /// Determines whether any claim of the provided type carries the provided string
        /// value, comparing flattened values ordinally so that duplicate-claim and
        /// array-claim representations of multi-value data behave identically.
        /// </summary>
        /// <param name="claimType">The canonical claim type to match.</param>
        /// <param name="value">The string value to match.</param>
        /// <returns><see langword="true" /> when a matching value exists; otherwise <see langword="false" />.</returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="claimType" /> is null or whitespace.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="value" /> is null.</exception>
        public bool HasClaim(string claimType, string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
            ArgumentNullException.ThrowIfNull(value);

            // Scan in place rather than materializing a flattened list — this sits on the
            // authorization hot path.
            for (var index = 0; index < claims.Count; index++)
            {
                var claim = claims[index];
                if (!string.Equals(claim.Type, claimType, StringComparison.Ordinal))
                {
                    continue;
                }

                if (claim.Value.TryGetString(out var single))
                {
                    if (string.Equals(single, value, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    continue;
                }

                if (claim.Value.TryGetArray(out var elements))
                {
                    for (var element = 0; element < elements.Count; element++)
                    {
                        if (elements[element].TryGetString(out var candidate) &&
                            string.Equals(candidate, value, StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
