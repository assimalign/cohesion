using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.IdentityModel.Protocols.Saml;

/// <summary>
/// Represents a SAML 2.0 <c>Conditions</c> element (SAML Core §2.5.1): the constraints under
/// which an assertion may be relied upon.
/// </summary>
/// <remarks>
/// Audience restrictions follow the SAML rule: an assertion satisfies the audience
/// constraint when, for <em>every</em> <c>AudienceRestriction</c>, the relying party matches
/// at least one of its audiences (AND across restrictions, OR within one). Each entry in
/// <see cref="AudienceRestrictions" /> is one restriction's set of audiences; an empty set is
/// a wire malformation that a validator treats as unsatisfiable, never vacuously true.
/// </remarks>
public sealed class SamlConditions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SamlConditions" /> class.
    /// </summary>
    /// <param name="notBefore">The instant before which the assertion is not valid.</param>
    /// <param name="notOnOrAfter">The instant at or after which the assertion is not valid.</param>
    /// <param name="audienceRestrictions">The audience restrictions, each a set of audiences. The sequence is copied.</param>
    /// <param name="oneTimeUse">Whether the assertion carries a one-time-use condition.</param>
    /// <param name="proxyRestrictionCount">The proxy restriction count, when present.</param>
    /// <param name="proxyRestrictionAudiences">The proxy restriction audiences. The sequence is copied.</param>
    /// <exception cref="ArgumentException">Thrown when an audience entry is null or whitespace.</exception>
    public SamlConditions(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notOnOrAfter = null,
        IEnumerable<IReadOnlyList<string>>? audienceRestrictions = null,
        bool oneTimeUse = false,
        int? proxyRestrictionCount = null,
        IEnumerable<string>? proxyRestrictionAudiences = null)
    {
        NotBefore = notBefore;
        NotOnOrAfter = notOnOrAfter;
        OneTimeUse = oneTimeUse;
        ProxyRestrictionCount = proxyRestrictionCount;

        AudienceRestrictions = SnapshotRestrictions(audienceRestrictions);
        ProxyRestrictionAudiences = SnapshotAudiences(proxyRestrictionAudiences, nameof(proxyRestrictionAudiences));
    }

    /// <summary>
    /// Gets the instant before which the assertion is not valid.
    /// </summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>
    /// Gets the instant at or after which the assertion is not valid.
    /// </summary>
    public DateTimeOffset? NotOnOrAfter { get; }

    /// <summary>
    /// Gets the audience restrictions, each a set of audiences. See the type remarks for the
    /// AND-across / OR-within satisfaction rule.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> AudienceRestrictions { get; }

    /// <summary>
    /// Gets a value indicating whether the assertion carries a one-time-use condition.
    /// </summary>
    public bool OneTimeUse { get; }

    /// <summary>
    /// Gets the proxy restriction count, when present.
    /// </summary>
    public int? ProxyRestrictionCount { get; }

    /// <summary>
    /// Gets the proxy restriction audiences.
    /// </summary>
    public IReadOnlyList<string> ProxyRestrictionAudiences { get; }

    /// <summary>
    /// Determines whether the provided relying party satisfies every audience restriction.
    /// An empty restriction set (a malformed restriction with no audiences) is never
    /// satisfied. When there are no restrictions the assertion is unrestricted.
    /// </summary>
    /// <param name="relyingParty">The relying party entity identifier to match.</param>
    /// <returns><see langword="true" /> when every restriction is satisfied; otherwise <see langword="false" />.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="relyingParty" /> is null or whitespace.</exception>
    public bool IsAudienceSatisfied(string relyingParty)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingParty);

        foreach (var restriction in AudienceRestrictions)
        {
            var matched = false;
            foreach (var audience in restriction)
            {
                if (string.Equals(audience, relyingParty, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            // A restriction the relying party is not in (including an empty restriction)
            // fails the whole assertion.
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<IReadOnlyList<string>> SnapshotRestrictions(IEnumerable<IReadOnlyList<string>>? restrictions)
    {
        if (restrictions is null)
        {
            return Array.Empty<IReadOnlyList<string>>();
        }

        var snapshot = new List<IReadOnlyList<string>>();
        foreach (var restriction in restrictions)
        {
            ArgumentNullException.ThrowIfNull(restriction, nameof(restrictions));
            snapshot.Add(SnapshotAudiences(restriction, nameof(restrictions)));
        }

        return new ReadOnlyCollection<IReadOnlyList<string>>(snapshot.ToArray());
    }

    private static IReadOnlyList<string> SnapshotAudiences(IEnumerable<string>? audiences, string parameterName)
    {
        if (audiences is null)
        {
            return Array.Empty<string>();
        }

        var snapshot = new List<string>();
        foreach (var audience in audiences)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(audience, parameterName);
            snapshot.Add(audience);
        }

        return new ReadOnlyCollection<string>(snapshot.ToArray());
    }
}
