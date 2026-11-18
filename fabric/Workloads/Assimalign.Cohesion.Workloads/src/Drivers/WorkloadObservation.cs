using System;
using System.Collections.Generic;
using System.Text.Json;

using Assimalign.Cohesion.Fabric;

namespace Assimalign.Cohesion.Workloads;

/// <summary>
/// What a workload driver observed on its platform after acting.
/// </summary>
/// <param name="State">The workload's provisioning state.</param>
/// <param name="Conditions">The workload's conditions, with the platform's own words on each platform cause.</param>
/// <param name="Outputs">The workload's outputs, including its platform read side under <c>platform.&lt;platform&gt;</c>.</param>
/// <param name="Faults">Every cause, classified by the platform's <see cref="IFabricFaultClassifier"/>.</param>
/// <param name="RequeueAfter">When to observe again without a change, if at all.</param>
public sealed record WorkloadObservation(
    ProvisioningState State,
    IReadOnlyList<FabricCondition> Conditions,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    IReadOnlyList<FabricFault> Faults,
    TimeSpan? RequeueAfter);
