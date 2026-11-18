using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// What one reconcile observed and wants. The provider never writes the store; the reconciler
/// writes this.
/// </summary>
/// <param name="State">The resource's provisioning state.</param>
/// <param name="Conditions">The resource's conditions.</param>
/// <param name="Outputs">The resource's outputs, readable by other resources through <c>$ref</c>.</param>
/// <param name="Children">The private children the resource should have, each named <c>{self}.{suffix}</c>. The reconciler creates, updates, and removes children to match.</param>
/// <param name="Faults">Every cause of a failed or stalled reconcile, not only the first.</param>
/// <param name="RequeueAfter">When to reconcile again without a change, if at all.</param>
public sealed record ReconcileResult(
    ProvisioningState State,
    IReadOnlyList<FabricCondition> Conditions,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    IReadOnlyList<ChildResource> Children,
    IReadOnlyList<FabricFault> Faults,
    TimeSpan? RequeueAfter);
