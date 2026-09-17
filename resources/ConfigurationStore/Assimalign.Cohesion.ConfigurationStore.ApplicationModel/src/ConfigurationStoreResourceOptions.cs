using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel;

/// <summary>
/// Provides deployer-owned planning overrides for a <see cref="ConfigurationStoreResource"/>.
/// </summary>
/// <remarks>
/// <see cref="ResourceOptions.Storage"/> exposes the persistent claim-size override.
/// <see cref="ResourceOptions.Replicas"/> remains available through the shared options shape,
/// but the ConfigurationStore planner requires one replica until replication is implemented.
/// </remarks>
public sealed class ConfigurationStoreResourceOptions : ResourceOptions
{
}
