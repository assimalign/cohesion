using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class LocalPlanCompilation
{
    public LocalPlanCompilation(
        ResourcePlan plan,
        IExecutableArtifact artifact,
        ResourceInputs inputs,
        IReadOnlyDictionary<string, string> environment,
        ResourceTelemetryInjection? telemetry = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        Telemetry = telemetry;
        ArgumentNullException.ThrowIfNull(environment);

        Environment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environment, StringComparer.Ordinal));
    }

    public ResourcePlan Plan { get; }

    public IExecutableArtifact Artifact { get; }

    public ResourceInputs Inputs { get; }

    internal ResourceTelemetryInjection? Telemetry { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }
}
