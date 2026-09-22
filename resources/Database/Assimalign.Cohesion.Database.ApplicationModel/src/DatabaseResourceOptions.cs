using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Provides deployer-owned planning overrides for a <see cref="DatabaseResource"/>.
/// </summary>
/// <remarks>
/// <see cref="ResourceOptions.Replicas"/> overrides the manifest replica count, and
/// <see cref="ResourceOptions.Storage"/> exposes the per-replica claim-size override.
/// </remarks>
public sealed class DatabaseResourceOptions : ResourceOptions
{
}
