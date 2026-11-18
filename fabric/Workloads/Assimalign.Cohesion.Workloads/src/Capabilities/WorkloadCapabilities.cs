using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// What a platform's workload driver does with each portable field. Capabilities make
/// degradation loud: every portable field resolves to Native, Emulated, or Unsupported.
/// </summary>
/// <param name="ProgramSource">Support for <see cref="WorkloadSource.Program"/>.</param>
/// <param name="ImageSource">Support for <see cref="WorkloadSource.Image"/>.</param>
/// <param name="MaxReplicas">The most replicas the platform runs; a value limit is a capability too.</param>
/// <param name="PersistentMounts">Support for <see cref="WorkloadSpec.Mounts"/>.</param>
/// <param name="Stateful">Support for <see cref="WorkloadSpec.Stateful"/>.</param>
/// <param name="OneShotTasks">Support for <see cref="WorkloadSpec.OneShot"/>.</param>
/// <param name="Probes">Support for <see cref="WorkloadSpec.Readiness"/>.</param>
/// <param name="Isolation">How far apart the platform runs workloads.</param>
public sealed record WorkloadCapabilities(
    Support ProgramSource,
    Support ImageSource,
    int MaxReplicas,
    Support PersistentMounts,
    Support Stateful,
    Support OneShotTasks,
    Support Probes,
    Isolation Isolation)
{
    /// <summary>
    /// Checks a workload spec against these capabilities, as stage 1 (emit) and stage 2
    /// (admission) of validation do.
    /// </summary>
    /// <param name="spec">The spec.</param>
    /// <param name="platform">The platform the capabilities belong to, for the messages.</param>
    /// <param name="policy">The environment's policy; emulation is an error unless it allows it.</param>
    /// <returns>An error when the source names both or neither of a program and an image, an error for each unsupported field and each emulated field the policy denies, a warning for each emulated field it allows, and an error when the replicas exceed <see cref="MaxReplicas"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spec"/> or <paramref name="policy"/> is <see langword="null"/>.</exception>
    public FabricDiagnostics Check(WorkloadSpec spec, PlatformName platform, FabricEnvironmentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(policy);

        var items = new List<FabricDiagnostic>();
        if ((spec.Source.Program is null) == (spec.Source.Image is null))
        {
            items.Add(new FabricDiagnostic(FabricDiagnosticSeverity.Error, "WorkloadSourceAmbiguous", ErrorMessages.WorkloadSourceAmbiguous, "spec.source"));
        }

        if (spec.Source.Program is not null)
        {
            Add(items, ProgramSource, platform, policy, "program sources", "spec.source.program");
        }

        if (spec.Source.Image is not null)
        {
            Add(items, ImageSource, platform, policy, "image sources", "spec.source.image");
        }

        if (spec.Mounts.Count > 0)
        {
            Add(items, PersistentMounts, platform, policy, "persistent mounts", "spec.mounts");
        }

        if (spec.Stateful)
        {
            Add(items, Stateful, platform, policy, "stateful workloads", "spec.stateful");
        }

        if (spec.OneShot)
        {
            Add(items, OneShotTasks, platform, policy, "one-shot workloads", "spec.oneShot");
        }

        if (spec.Readiness is not null)
        {
            Add(items, Probes, platform, policy, "readiness probes", "spec.readiness");
        }

        if (spec.Replicas < 0)
        {
            items.Add(new FabricDiagnostic(FabricDiagnosticSeverity.Error, "ReplicasNegative", ErrorMessages.ReplicasNegative, "spec.replicas"));
        }
        else if (spec.Replicas > MaxReplicas)
        {
            items.Add(new FabricDiagnostic(
                FabricDiagnosticSeverity.Error,
                "ReplicasExceedMaximum",
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ReplicasExceedMaximum, spec.Replicas, platform, MaxReplicas),
                "spec.replicas"));
        }

        return items.Count == 0 ? FabricDiagnostics.None : new FabricDiagnostics(items);
    }

    private static void Add(List<FabricDiagnostic> items, Support support, PlatformName platform, FabricEnvironmentPolicy policy, string field, string path)
    {
        switch (support.Level)
        {
            case SupportLevel.Unsupported:
                items.Add(new FabricDiagnostic(
                    FabricDiagnosticSeverity.Error,
                    "CapabilityUnsupported",
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.CapabilityUnsupported, platform, field),
                    path));
                break;
            case SupportLevel.Emulated when !policy.AllowEmulation:
                items.Add(new FabricDiagnostic(
                    FabricDiagnosticSeverity.Error,
                    "CapabilityEmulationDenied",
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.CapabilityEmulationDenied, platform, field),
                    path));
                break;
            case SupportLevel.Emulated:
                items.Add(new FabricDiagnostic(
                    FabricDiagnosticSeverity.Warning,
                    "CapabilityEmulated",
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.CapabilityEmulated, platform, field),
                    path));
                break;
        }
    }
}
