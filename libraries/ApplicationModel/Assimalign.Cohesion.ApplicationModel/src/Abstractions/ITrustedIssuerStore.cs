using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Persists the peer issuers an application trusts. Registered through
/// <see cref="ApplicationProviders.TrustStore"/>.
/// </summary>
/// <remarks>
/// Without a registered store the gateway keeps trusted issuers in a Local-only file under the
/// application's state directory and rejects trust grants in every other environment.
/// </remarks>
public interface ITrustedIssuerStore
{
    /// <summary>
    /// Gets the manifest kind the bound resource must have, or <see langword="null"/> when the
    /// store does not require a model resource.
    /// </summary>
    string? ResourceKind { get; }

    /// <summary>
    /// Reads the persisted trusted issuers.
    /// </summary>
    /// <param name="store">
    /// The connection to the bound store resource, or <see langword="null"/> when the binding names
    /// no resource.
    /// </param>
    /// <param name="cancellationToken">Signals that the read should be abandoned.</param>
    /// <returns>
    /// The persisted issuers, or <see langword="null"/> when nothing has been persisted yet.
    /// </returns>
    ValueTask<IReadOnlyList<TrustedIssuer>?> ReadAsync(
        ResourceProviderConnection? store,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists one trusted issuer grant.
    /// </summary>
    /// <param name="store">
    /// The connection to the bound store resource, or <see langword="null"/> when the binding names
    /// no resource.
    /// </param>
    /// <param name="owner">
    /// The ownership identity recorded with the grant; the gateway passes the model's
    /// <see cref="IApplicationModel.Owner"/>.
    /// </param>
    /// <param name="issuer">The issuer to trust.</param>
    /// <param name="cancellationToken">Signals that the write should be abandoned.</param>
    /// <returns>A task that completes when the grant is persisted.</returns>
    ValueTask AddAsync(
        ResourceProviderConnection? store,
        string owner,
        TrustedIssuer issuer,
        CancellationToken cancellationToken = default);
}
