using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides deployer-owned planning overrides for an <see cref="IdentityHubResource"/>.
/// </summary>
/// <remarks>
/// <see cref="ResourceOptions.Storage"/> exposes the persistent claim-size override.
/// IdentityHub remains a single-replica workload until a replication protocol is defined.
/// </remarks>
public sealed class IdentityHubResourceOptions : ResourceOptions
{
}
