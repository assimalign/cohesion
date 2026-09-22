using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion configuration store in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled configuration-store executable, and its realization plan
/// contains no platform-specific objects or runtime configuration-store dependencies.
/// </remarks>
public sealed class ConfigurationStoreResource : PlannedResource
{
    private const string ConfigurationStorePlannerName = "ConfigurationStore planner";

    /// <summary>
    /// Initializes a new <see cref="ConfigurationStoreResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The configuration-store resource manifest.</param>
    /// <param name="options">
    /// Optional deployer-owned replica and storage overrides; manifest values are used
    /// when no override is supplied.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public ConfigurationStoreResource(
        ResourceManifest manifest,
        ConfigurationStoreResourceOptions? options = null)
        : base(manifest, options ?? new ConfigurationStoreResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => ConfigurationStorePlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return ConfigurationStorePlanner.CreatePlan(context);
    }
}
