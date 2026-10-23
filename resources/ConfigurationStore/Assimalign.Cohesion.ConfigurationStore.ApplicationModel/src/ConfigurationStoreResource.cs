using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion configuration store in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled configuration-store executable, and its realization plan
/// contains no platform-specific objects or runtime configuration-store dependencies.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("ConfigurationStoreResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly ConfigurationStoreResource _target;

        public DebugView(ConfigurationStoreResource value)
        {
            _target = value;
        }

        public string PlannerName => _target.PlannerName;
        public global::Assimalign.Cohesion.ApplicationModel.ResourceName Name => _target.Name;
        public global::Assimalign.Cohesion.ApplicationModel.ResourceManifest Manifest => _target.Manifest;
        public global::Assimalign.Cohesion.ApplicationModel.IResourceOptions Options => _target.Options;
        public string Artifact => _target.Artifact;
        public global::System.Collections.Generic.IReadOnlyDictionary<string, string> EnvironmentVariables => _target.EnvironmentVariables;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.ApplicationModel.ResourceEndpoint> Endpoints => _target.Endpoints;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.ApplicationModel.ResourceMount> Mounts => _target.Mounts;
    }
}
