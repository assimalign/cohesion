using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion identity hub in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Its manifest is
/// produced by an enabled identity-hub executable, and its realization plan contains
/// no platform-specific objects or IdentityHub runtime dependencies.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("IdentityHubResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class IdentityHubResource : PlannedResource
{
    private const string IdentityHubPlannerName = "IdentityHub planner";

    /// <summary>
    /// Initializes a new <see cref="IdentityHubResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The identity-hub resource manifest.</param>
    /// <param name="options">Optional deployer-owned storage overrides.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public IdentityHubResource(
        ResourceManifest manifest,
        IdentityHubResourceOptions? options = null)
        : base(manifest, options ?? new IdentityHubResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => IdentityHubPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return IdentityHubPlanner.CreatePlan(context);
    }

    private sealed class DebugView
    {
        private readonly IdentityHubResource _target;

        public DebugView(IdentityHubResource value)
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
