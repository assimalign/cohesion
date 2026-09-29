using System;
using System.Net.Http;

using Assimalign.Cohesion.SecretStore.Client;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// The SecretStore wire vocabulary the providers share — resource kind, command kinds, reserved
/// secret and certificate names — and the one place a provider turns a gateway-supplied
/// <see cref="ResourceProviderConnection"/> into a SecretStore client.
/// </summary>
/// <remarks>
/// Every value here is the literal the gateway used before the provider seams existed; moving them
/// out of <c>Assimalign.Cohesion.ApplicationModel.Gateway</c> is the point of this package.
/// </remarks>
internal static class SecretStoreProtocol
{
    /// <summary>The manifest kind of a SecretStore resource.</summary>
    internal const string ResourceKind = "SecretStore";

    /// <summary>The command kind whose declared source the gateway resolves before delivery.</summary>
    internal const string AddSecretCommandKind = "secretstore.add-secret";

    /// <summary>The command kind that persists one trusted peer issuer in the store.</summary>
    internal const string TrustAddCommandKind = "cohesion.trust.add";

    /// <summary>The reserved secret path under which the store exports its trusted issuers.</summary>
    internal const string TrustedIssuersPath = "trusted-issuers.json";

    /// <summary>The certificate name that returns the store authority's root certificate.</summary>
    internal const string RootCertificateName = "ca/root";

    /// <summary>The certificate namespace in which the store issues leaves on first resolution.</summary>
    internal const string LeafCertificatePrefix = "certs/";

    /// <summary>
    /// Returns <paramref name="connection"/> after checking it connects to a SecretStore resource.
    /// </summary>
    /// <param name="connection">The gateway-supplied connection, or <see langword="null"/>.</param>
    /// <param name="role">The provider role, for the failure message.</param>
    /// <param name="paramName">The name of the argument that carried the connection.</param>
    /// <returns>The validated connection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The connection's resource is not a SecretStore.</exception>
    internal static ResourceProviderConnection RequireStore(
        ResourceProviderConnection? connection,
        string role,
        string paramName)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(
                paramName,
                $"The SecretStore {role} calls a {ResourceKind} resource's control plane, but no connection " +
                "was supplied. Bind the provider to the SecretStore resource (builder.UseSecretStore(store)) " +
                "so the gateway connects it.");
        }

        EnsureSecretStore(connection, paramName);
        return connection;
    }

    /// <summary>
    /// Checks that <paramref name="connection"/> connects to a SecretStore resource.
    /// </summary>
    /// <param name="connection">The gateway-supplied connection.</param>
    /// <param name="paramName">The name of the argument that carried the connection.</param>
    /// <exception cref="ArgumentException">The connection's resource is not a SecretStore.</exception>
    internal static void EnsureSecretStore(ResourceProviderConnection connection, string paramName)
    {
        // Manifest kinds compare case-insensitively, the rule the gateway and Build() validation use.
        if (!string.Equals(connection.ResourceKind, ResourceKind, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Resource '{connection.Resource}' is kind '{connection.ResourceKind}', but the SecretStore " +
                $"providers call {ResourceKind} resources only.",
                paramName);
        }
    }

    /// <summary>
    /// Creates a SecretStore client bound to the connection's control plane and bearer credential.
    /// </summary>
    /// <param name="connection">The validated connection.</param>
    /// <param name="transport">The caller-owned transport; the caller disposes it.</param>
    /// <returns>A client whose routes are relative to the connection's control-plane address.</returns>
    /// <exception cref="ArgumentException">
    /// The control-plane address is not an HTTP(S) endpoint, or the bearer credential is blank.
    /// </exception>
    internal static ISecretStoreClient CreateClient(ResourceProviderConnection connection, HttpMessageInvoker transport) =>
        SecretStoreClient.CreateForControlPlane(
            connection.ControlPlaneAddress,
            new ClientCredential(connection.BearerCredential),
            transport);
}
