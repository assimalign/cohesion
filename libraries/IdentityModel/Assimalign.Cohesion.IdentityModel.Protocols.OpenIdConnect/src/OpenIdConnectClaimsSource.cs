using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect;

/// <summary>
/// Represents an unresolved aggregated or distributed claims reference
/// (<c>_claim_names</c> / <c>_claim_sources</c>, OpenID Connect Core §5.6.2). Unresolved
/// references never enter the canonical claim collection; this type preserves them so
/// resolution layers can fetch them and normalization can map the resolved claims with
/// their third-party issuer. Resolution itself (HTTP, JWT parsing) is out of scope for
/// this library.
/// </summary>
public sealed class OpenIdConnectClaimsSource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OpenIdConnectClaimsSource" /> class.
    /// </summary>
    /// <param name="sourceId">The source member name from <c>_claim_sources</c>.</param>
    /// <param name="claimNames">The claim names this source provides, from <c>_claim_names</c>.</param>
    /// <param name="jwt">The aggregated claims JWT, as the raw compact string, for the aggregated form.</param>
    /// <param name="endpoint">The claims endpoint, as the exact wire string, for the distributed form.</param>
    /// <param name="accessToken">The access token for the distributed endpoint, when provided.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="sourceId" /> is null or whitespace, or when a claim
    /// name entry is null or whitespace.
    /// </exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="claimNames" /> is null.</exception>
    public OpenIdConnectClaimsSource(
        string sourceId,
        IEnumerable<string> claimNames,
        string? jwt = null,
        string? endpoint = null,
        string? accessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(claimNames);

        var names = new List<string>();
        foreach (var name in claimNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(claimNames));
            names.Add(name);
        }

        SourceId = sourceId;
        ClaimNames = new System.Collections.ObjectModel.ReadOnlyCollection<string>(names.ToArray());
        Jwt = jwt;
        Endpoint = endpoint;
        AccessToken = accessToken;
    }

    /// <summary>
    /// Gets the source member name from <c>_claim_sources</c>.
    /// </summary>
    public string SourceId { get; }

    /// <summary>
    /// Gets the claim names this source provides.
    /// </summary>
    public IReadOnlyList<string> ClaimNames { get; }

    /// <summary>
    /// Gets the aggregated claims JWT, as the raw compact string; null for the
    /// distributed form.
    /// </summary>
    public string? Jwt { get; }

    /// <summary>
    /// Gets the claims endpoint, as the exact wire string; null for the aggregated form.
    /// </summary>
    public string? Endpoint { get; }

    /// <summary>
    /// Gets the access token for the distributed endpoint, when provided.
    /// </summary>
    public string? AccessToken { get; }
}
