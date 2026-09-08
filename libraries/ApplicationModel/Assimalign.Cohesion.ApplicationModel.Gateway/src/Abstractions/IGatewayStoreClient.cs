using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Provides the narrow gateway-side operations used to resolve protected mount sources and
/// maintain an application's trusted issuers through its own store control plane.
/// </summary>
/// <remarks>
/// The default implementation delegates to the Hosting-free SecretStore and
/// ConfigurationStore client packages. Gateways may replace this seam for testing or for a
/// platform-native transport without referencing either area's Hosting package.
/// </remarks>
public interface IGatewayStoreClient
{
    /// <summary>Reads secret bytes from a SecretStore resource.</summary>
    /// <param name="endpoint">The observed SecretStore control-plane endpoint.</param>
    /// <param name="credential">The bootstrap credential issued for that resource.</param>
    /// <param name="path">The secret path.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The secret bytes.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadSecretAsync(
        Uri endpoint,
        string credential,
        string path,
        CancellationToken cancellationToken = default);

    /// <summary>Requests a PEM-encoded certificate leaf from a SecretStore resource.</summary>
    /// <param name="endpoint">The observed SecretStore control-plane endpoint.</param>
    /// <param name="credential">The bootstrap credential issued for that resource.</param>
    /// <param name="name">The certificate name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The PEM-encoded certificate leaf.</returns>
    ValueTask<string> ReadCertificateAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a namespace from a ConfigurationStore resource.</summary>
    /// <param name="endpoint">The observed ConfigurationStore control-plane endpoint.</param>
    /// <param name="credential">The bootstrap credential issued for that resource.</param>
    /// <param name="name">The configuration namespace.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The namespace values.</returns>
    ValueTask<IReadOnlyDictionary<string, string?>> ReadConfigurationAsync(
        Uri endpoint,
        string credential,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or replaces one trusted issuer through the application's own SecretStore.
    /// </summary>
    /// <param name="endpoint">The observed application SecretStore endpoint.</param>
    /// <param name="credential">The bootstrap credential issued for that resource.</param>
    /// <param name="owner">The application owner issuing the command.</param>
    /// <param name="issuer">The trusted issuer name.</param>
    /// <param name="publicKey">The issuer's public JSON Web Key.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the store accepts the command.</returns>
    ValueTask StoreTrustedIssuerAsync(
        Uri endpoint,
        string credential,
        string owner,
        string issuer,
        ReadOnlyMemory<byte> publicKey,
        CancellationToken cancellationToken = default);
}
