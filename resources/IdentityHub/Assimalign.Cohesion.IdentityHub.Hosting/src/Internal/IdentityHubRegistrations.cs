using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.IdentityHub.Hosting.Internal;

/// <summary>
/// The declared audiences and clients, resolved once from their registrations and validated
/// against each other.
/// </summary>
internal sealed class IdentityHubRegistrations
{
    private IdentityHubRegistrations(
        HashSet<string> audiences,
        Dictionary<string, IdentityHubClientRegistration> clients)
    {
        Audiences = audiences;
        Clients = clients;
    }

    internal IReadOnlyCollection<string> Audiences { get; }

    internal IReadOnlyDictionary<string, IdentityHubClientRegistration> Clients { get; }

    /// <summary>
    /// Validates the registered audiences and clients.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// An audience or client is registered twice, a client enables no grant, or a client allows an
    /// undeclared audience.
    /// </exception>
    internal static IdentityHubRegistrations Create(
        IEnumerable<IdentityHubAudience> audiences,
        IEnumerable<IdentityHubClientRegistration> clients)
    {
        var declaredAudiences = new HashSet<string>(StringComparer.Ordinal);
        foreach (IdentityHubAudience audience in audiences)
        {
            if (!declaredAudiences.Add(audience.Value))
            {
                throw new InvalidOperationException($"IdentityHub audience '{audience.Value}' is already declared.");
            }
        }

        var registeredClients = new Dictionary<string, IdentityHubClientRegistration>(StringComparer.Ordinal);
        foreach (IdentityHubClientRegistration client in clients)
        {
            if (!registeredClients.TryAdd(client.ClientId, client))
            {
                throw new InvalidOperationException($"IdentityHub client '{client.ClientId}' is already registered.");
            }

            if (!client.AllowsClientCredentials && !client.AllowDeviceAuthorization)
            {
                throw new InvalidOperationException(
                    $"IdentityHub client '{client.ClientId}' must enable client credentials or device authorization.");
            }

            if (client.Audiences.Count is 0)
            {
                throw new InvalidOperationException(
                    $"IdentityHub client '{client.ClientId}' must allow at least one declared audience.");
            }

            for (int index = 0; index < client.Audiences.Count; index++)
            {
                string audience = client.Audiences[index];
                if (!declaredAudiences.Contains(audience))
                {
                    throw new InvalidOperationException(
                        $"IdentityHub client '{client.ClientId}' refers to undeclared audience '{audience}'.");
                }
            }
        }

        return new IdentityHubRegistrations(declaredAudiences, registeredClients);
    }
}
