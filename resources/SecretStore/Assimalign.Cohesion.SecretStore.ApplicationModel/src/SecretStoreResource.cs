using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion secret store in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled secret-store executable, and its realization plan contains
/// no platform-specific objects or runtime secret-store dependencies.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SecretStoreResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
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

    private sealed class DebugView
    {
        private readonly SecretStoreResource _target;

        public DebugView(SecretStoreResource value)
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
