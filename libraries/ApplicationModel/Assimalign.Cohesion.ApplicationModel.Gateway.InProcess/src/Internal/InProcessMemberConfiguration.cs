using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

[global::System.Diagnostics.DebuggerDisplay("InProcessMemberConfiguration")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class InProcessMemberConfiguration
{
    internal InProcessMemberConfiguration(
        IResourceControlContext control,
        InProcessResourceArtifact artifact,
        ResourceContext resourceContext,
        IReadOnlyList<ResourceEndpoint> observedEndpoints,
        InProcessProbeConfiguration startupProbe,
        InProcessProbeConfiguration readinessProbe,
        InProcessProbeConfiguration livenessProbe,
        RestartPolicy restartPolicy)
    {
        Control = control;
        Artifact = artifact;
        ResourceContext = resourceContext;
        ObservedEndpoints = observedEndpoints;
        StartupProbe = startupProbe;
        ReadinessProbe = readinessProbe;
        LivenessProbe = livenessProbe;
        RestartPolicy = restartPolicy;
    }

    internal IResourceControlContext Control { get; }

    internal InProcessResourceArtifact Artifact { get; }

    internal ResourceContext ResourceContext { get; }

    internal IReadOnlyList<ResourceEndpoint> ObservedEndpoints { get; }

    internal InProcessProbeConfiguration StartupProbe { get; }

    internal InProcessProbeConfiguration ReadinessProbe { get; }

    internal InProcessProbeConfiguration LivenessProbe { get; }

    internal RestartPolicy RestartPolicy { get; }

    private sealed class DebugView
    {
        public DebugView(InProcessMemberConfiguration value)
        {
        }
    }
}

internal sealed record InProcessProbeConfiguration(
    ProbeMapping Mapping,
    bool IsDefaultControlPlane);
