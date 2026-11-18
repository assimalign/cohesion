using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// The desired state of a <c>Cohesion.Workloads/workloads</c> resource: the portable core (tier
/// T0) every platform implements, emulates, or rejects, plus the typed platform blocks (tier T1).
/// </summary>
/// <remarks>
/// Init properties rather than a positional record, so the spec can gain portable fields without
/// breaking callers. A field moves into the portable core once two platforms implement it natively.
/// </remarks>
public sealed record WorkloadSpec
{
    /// <summary>
    /// Gets what the workload runs.
    /// </summary>
    public required WorkloadSource Source { get; init; }

    /// <summary>
    /// Gets how many instances run. A platform's <see cref="WorkloadCapabilities.MaxReplicas"/> bounds it.
    /// </summary>
    public int Replicas { get; init; } = 1;

    /// <summary>
    /// Gets the ports the workload listens on.
    /// </summary>
    public IReadOnlyList<WorkloadPort> Ports { get; init; } = [];

    /// <summary>
    /// Gets the configuration the workload receives, by configuration key. A value is a literal or
    /// a <c>$ref</c> to another resource's output.
    /// </summary>
    public IReadOnlyDictionary<string, FabricValue<string>> Settings { get; init; } = new Dictionary<string, FabricValue<string>>();

    /// <summary>
    /// Gets a value indicating whether each instance keeps a stable identity and storage across restarts.
    /// </summary>
    public bool Stateful { get; init; }

    /// <summary>
    /// Gets a value indicating whether the workload runs to completion once instead of staying up.
    /// </summary>
    public bool OneShot { get; init; }

    /// <summary>
    /// Gets the volumes the workload mounts, if any.
    /// </summary>
    public IReadOnlyList<WorkloadMount> Mounts { get; init; } = [];

    /// <summary>
    /// Gets the readiness probe, if any.
    /// </summary>
    public WorkloadProbe? Readiness { get; init; }

    /// <summary>
    /// Gets the typed platform blocks, by platform. A driver sees only its own platform's block,
    /// and each block carries its own <c>apiVersion</c>.
    /// </summary>
    public IReadOnlyDictionary<PlatformName, JsonElement> Platforms { get; init; } = new Dictionary<PlatformName, JsonElement>();
}
