using System;
using System.Globalization;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Represents a manifest-backed Cohesion Web workload in an application graph.
/// </summary>
/// <remarks>
/// The resource contains only platform-neutral orchestration facts. Platform gateways
/// compile its <see cref="ResourcePlan"/> without loading this assembly.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("WebResource {Name,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class WebResource : PlannedResource
{
    private const string WebPlannerName = "Web planner";

    /// <summary>
    /// Initializes a new <see cref="WebResource"/> from a build-produced manifest.
    /// </summary>
    /// <param name="manifest">The Web resource manifest.</param>
    /// <param name="options">Optional replica overrides.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="manifest"/> is <see langword="null"/>.
    /// </exception>
    public WebResource(
        ResourceManifest manifest,
        WebResourceOptions? options = null)
        : base(manifest, options ?? new WebResourceOptions())
    {
    }

    /// <inheritdoc />
    public override string PlannerName => WebPlannerName;

    /// <inheritdoc />
    public override ResourcePlan CreatePlan(PlanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Manifest.Name.Equals(context.Manifest.Name))
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlanningContextResourceMismatch, context.Manifest.Name, Manifest.Name));
        }

        return WebPlanner.CreatePlan(context);
    }

    private sealed class DebugView
    {
        private readonly WebResource _target;

        public DebugView(WebResource value)
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
