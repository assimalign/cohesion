using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel;

/// <summary>
/// Provides deployer-owned planning overrides for a <see cref="SecretStoreResource"/>.
/// </summary>
/// <remarks>
/// <see cref="ResourceOptions.Storage"/> exposes the persistent data claim-size override.
/// The inherited <see cref="ResourceOptions.Replicas"/> property must be unset or one;
/// the planner rejects multiple replicas until a replication protocol is available.
/// </remarks>
public sealed class SecretStoreResourceOptions : ResourceOptions
{
}
