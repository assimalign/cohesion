using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

internal sealed class InProcessPlanCompilation
{
    internal InProcessPlanCompilation(
        ResourcePlan plan,
        InProcessResourceArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyList<ResourceDependencyObservation> observedDependencies,
        IReadOnlyDictionary<string, string> ambientValues,
        IResourceTelemetry? telemetry = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        Telemetry = telemetry;
        ArgumentNullException.ThrowIfNull(observedDependencies);
        ArgumentNullException.ThrowIfNull(ambientValues);

        var observations = new ResourceDependencyObservation[observedDependencies.Count];
        for (int index = 0; index < observations.Length; index++)
        {
            observations[index] = observedDependencies[index];
        }
        ObservedDependencies = observations;
        AmbientValues = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(ambientValues, StringComparer.Ordinal));
    }

    internal ResourcePlan Plan { get; }

    internal InProcessResourceArtifact Artifact { get; }

    internal ResourceInputs Inputs { get; }

    internal IResourceTelemetry? Telemetry { get; }

    internal IReadOnlyList<ResourceDependencyObservation> ObservedDependencies { get; }

    internal IReadOnlyDictionary<string, string> AmbientValues { get; }
}
