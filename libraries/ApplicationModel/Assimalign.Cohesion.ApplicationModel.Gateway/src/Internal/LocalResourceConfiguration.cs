using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

internal sealed class LocalResourceConfiguration
{
    public LocalResourceConfiguration(
        IApplicationResource resource,
        IExecutableArtifact artifact,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<ResourceEndpoint> observedEndpoints,
        IProbeSpec? startupProbe,
        IProbeSpec? readinessProbe,
        IProbeSpec? livenessProbe,
        string? probeStartMarker,
        bool markerIsReadiness,
        RestartPolicy restartPolicy,
        TimeSpan stopGrace,
        bool useStopEvent)
    {
        Resource = resource;
        Artifact = artifact;
        Environment = environment;
        ObservedEndpoints = observedEndpoints;
        StartupProbe = startupProbe;
        ReadinessProbe = readinessProbe;
        LivenessProbe = livenessProbe;
        ProbeStartMarker = probeStartMarker;
        MarkerIsReadiness = markerIsReadiness;
        RestartPolicy = restartPolicy;
        StopGrace = stopGrace;
        UseStopEvent = useStopEvent;
    }

    public IApplicationResource Resource { get; }

    public IExecutableArtifact Artifact { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    public IReadOnlyList<ResourceEndpoint> ObservedEndpoints { get; }

    public IProbeSpec? StartupProbe { get; }

    public IProbeSpec? ReadinessProbe { get; }

    public IProbeSpec? LivenessProbe { get; }

    public string? ProbeStartMarker { get; }

    public bool MarkerIsReadiness { get; }

    public RestartPolicy RestartPolicy { get; }

    public TimeSpan StopGrace { get; }

    public bool UseStopEvent { get; }
}
