using System;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Represents a manifest-backed LogSpace workload in an application graph.</summary>
[global::System.Diagnostics.DebuggerDisplay("LogSpaceResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class LogSpaceResource : PlannedResource
{
    private const string LogSpacePlannerName = "LogSpace planner";

    /// <summary>Initializes a LogSpace resource.</summary>
    /// <param name="manifest">The LogSpace resource manifest.</param>
    /// <param name="options">Optional resource planning overrides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
    public LogSpaceResource(
        ResourceManifest manifest,
        LogSpaceResourceOptions? options = null)
        : base(manifest, options ?? new LogSpaceResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => LogSpacePlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                $"Planning context resource '{context.Manifest.Name}' does not match '{Manifest.Name}'.");
        }

        return LogSpacePlanner.CreatePlan(context);
    }

    private sealed class DebugView
    {
        private readonly LogSpaceResource _target;

        public DebugView(LogSpaceResource value)
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
