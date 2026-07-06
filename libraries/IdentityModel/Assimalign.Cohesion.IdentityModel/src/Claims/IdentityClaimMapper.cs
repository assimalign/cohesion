using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel;

/// <summary>
/// Canonicalizes claim collections: re-types claims whose wire names have a strictly equivalent
/// canonical claim type (see <see cref="IdentityClaimMappings" />), preserving values
/// byte-identically and provenance losslessly. This is the cross-protocol seam that lets
/// resources consume one normalized identity surface — an OpenID Connect ID token and a SAML
/// assertion asserting the same data canonicalize to the same claim types, while unmapped claims
/// pass through unchanged and protocol meaning is never fabricated.
/// </summary>
/// <remarks>
/// <para>
/// Mapping is name-based on <see cref="IIdentityClaim.Type" /> only — provenance-blind (the key
/// namespaces are disjoint by construction, and OpenID Connect providers legitimately emit
/// <c>urn:oid:…</c> claim names that should normalize identically) — with no friendly-name
/// fallback (SAML defines <c>FriendlyName</c> as display-only; matching on it would be an
/// aliasing vector).
/// </para>
/// <para>
/// The mapper is idempotent by construction: every mapping is resolved to its transitive fixed
/// point at materialization (a chain <c>a → b</c>, <c>b → c</c> stores <c>a → c</c>), a cyclic
/// chain fails materialization, and no mapping may target a structural claim
/// (<c>sub</c>/<c>iss</c>/<c>aud</c>/<c>exp</c>/<c>iat</c>/<c>nbf</c>/<c>jti</c>) — subject
/// identity flows only through the family's pinned NameID recipes, never the mapper.
/// Canonicalization never transforms values, merges duplicates, or drops claims: two wire names
/// mapping to one canonical type simply yield duplicate claims of that type, the family's
/// canonical multi-value representation, with provenance disambiguating the sources.
/// </para>
/// </remarks>
public sealed class IdentityClaimMapper
{
    private static readonly FrozenSet<string> structuralTargets = new[]
    {
        IdentityClaimTypes.Subject,
        IdentityClaimTypes.Issuer,
        IdentityClaimTypes.Audience,
        IdentityClaimTypes.ExpirationTime,
        IdentityClaimTypes.IssuedAt,
        IdentityClaimTypes.NotBefore,
        IdentityClaimTypes.JwtId,
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly FrozenDictionary<string, string> _mappings;

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityClaimMapper" /> class by
    /// snapshotting and resolving the provided descriptor.
    /// </summary>
    /// <param name="descriptor">The mapper contents.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when a custom mapping name or target is null or whitespace.</exception>
    /// <exception cref="IdentityModelException">
    /// Thrown when a mapping chain is cyclic, or a mapping resolves to a structural claim type.
    /// </exception>
    public IdentityClaimMapper(IdentityClaimMapperDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        _mappings = Resolve(descriptor);
    }

    /// <summary>
    /// Gets the mapper carrying only the <see cref="IdentityClaimMappings.Default" /> table.
    /// </summary>
    public static IdentityClaimMapper Default { get; } = new(new IdentityClaimMapperDescriptor());

    /// <summary>
    /// Gets the resolved wire-name to canonical-type mappings this mapper applies.
    /// </summary>
    public IReadOnlyDictionary<string, string> Mappings => _mappings;

    /// <summary>
    /// Canonicalizes the provided claims: claims whose type has a mapping are re-typed to the
    /// canonical name with their value byte-identical and their provenance preserved (the
    /// pre-mapping wire name fills <see cref="IdentityClaimProvenance.OriginalType" /> only when
    /// the producing package did not already record one — normalization never erases the
    /// source); all other claims pass through as the same instances. When nothing maps, the
    /// input collection itself is returned, so canonicalizing twice equals canonicalizing once.
    /// </summary>
    /// <param name="claims">The claims to canonicalize.</param>
    /// <returns>The canonicalized collection, or <paramref name="claims" /> when nothing mapped.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="claims" /> is null.</exception>
    public IIdentityClaimCollection Canonicalize(IIdentityClaimCollection claims)
    {
        ArgumentNullException.ThrowIfNull(claims);

        List<IIdentityClaim>? canonicalized = null;

        for (var index = 0; index < claims.Count; index++)
        {
            var claim = claims[index];

            if (!_mappings.TryGetValue(claim.Type, out var canonicalType))
            {
                canonicalized?.Add(claim);
                continue;
            }

            if (canonicalized is null)
            {
                // First remap: copy the untouched prefix as the same instances.
                canonicalized = new List<IIdentityClaim>(claims.Count);
                for (var prefix = 0; prefix < index; prefix++)
                {
                    canonicalized.Add(claims[prefix]);
                }
            }

            canonicalized.Add(new IdentityClaim(
                canonicalType,
                claim.Value,
                claim.Issuer,
                MergeProvenance(claim)));
        }

        return canonicalized is null ? claims : new IdentityClaimCollection(canonicalized);
    }

    private static IdentityClaimProvenance MergeProvenance(IIdentityClaim claim)
    {
        var provenance = claim.Provenance;

        if (provenance is null)
        {
            // A hand-constructed claim with no provenance: record the pre-mapping wire name
            // under the honest unknown protocol — never fabricate a protocol.
            return new IdentityClaimProvenance(AuthenticationProtocol.Unknown, originalType: claim.Type);
        }

        if (provenance.OriginalType is not null)
        {
            // The producing package already recorded the original wire name; keep it verbatim.
            return provenance;
        }

        return new IdentityClaimProvenance(
            provenance.Protocol,
            originalType: claim.Type,
            originalIssuer: provenance.OriginalIssuer,
            originalValueType: provenance.OriginalValueType,
            originalNameFormat: provenance.OriginalNameFormat,
            originalFriendlyName: provenance.OriginalFriendlyName);
    }

    private static FrozenDictionary<string, string> Resolve(IdentityClaimMapperDescriptor descriptor)
    {
        // Merge: defaults first, customs overwrite (explicit caller intent beats shipped
        // defaults — real providers repurpose standard names).
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (descriptor.IncludeDefaultMappings)
        {
            foreach (var (name, target) in IdentityClaimMappings.Default)
            {
                merged[name] = target;
            }
        }

        foreach (var (name, target) in descriptor.CustomMappings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(descriptor));
            ArgumentException.ThrowIfNullOrWhiteSpace(target, nameof(descriptor));
            merged[name] = target;
        }

        // Resolve each name to its transitive fixed point so application is idempotent by
        // construction; identity resolutions (suppressed defaults) drop out entirely.
        var resolved = new Dictionary<string, string>(merged.Count, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in merged.Keys)
        {
            visited.Clear();
            visited.Add(name);

            var target = merged[name];
            while (merged.TryGetValue(target, out var next) && !string.Equals(next, target, StringComparison.Ordinal))
            {
                if (!visited.Add(target))
                {
                    throw new IdentityModelException(
                        $"The claim mapping chain starting at '{name}' is cyclic and cannot be resolved.");
                }

                target = next;
            }

            if (string.Equals(name, target, StringComparison.Ordinal))
            {
                continue;
            }

            if (structuralTargets.Contains(target))
            {
                throw new IdentityModelException(
                    $"The claim mapping '{name}' resolves to the structural claim '{target}'. Subject and envelope " +
                    "claims flow only through the family's pinned recipes, never the mapper.");
            }

            resolved[name] = target;
        }

        return resolved.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
