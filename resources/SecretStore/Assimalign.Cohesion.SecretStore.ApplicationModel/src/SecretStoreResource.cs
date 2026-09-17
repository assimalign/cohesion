using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.SecretStore.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion secret store in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled secret-store executable, and its realization plan contains
/// no platform-specific objects or runtime secret-store dependencies.
/// </remarks>
public sealed class SecretStoreResource : PlannedResource
{
    private const string SecretStorePlannerName = "SecretStore planner";

    /// <summary>
    /// Initializes a new <see cref="SecretStoreResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The secret-store resource manifest.</param>
    /// <param name="options">
    /// Optional deployer-owned planning overrides. Storage size may be overridden, but
    /// the effective replica count must remain one until replication is supported.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public SecretStoreResource(
        ResourceManifest manifest,
        SecretStoreResourceOptions? options = null)
        : base(manifest, options ?? new SecretStoreResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => SecretStorePlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return SecretStorePlanner.CreatePlan(context);
    }
}
