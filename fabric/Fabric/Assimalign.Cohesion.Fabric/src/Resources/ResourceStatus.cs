using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.Fabric;

/// <summary>
/// The observed state of a resource, written only by its reconcile.
/// </summary>
/// <param name="ObservedGeneration">The <see cref="ResourceMetadata.Generation"/> the last reconcile observed.</param>
/// <param name="ProvisioningState">Where the resource is in its lifecycle.</param>
/// <param name="Conditions">The current conditions, each with its own reason and, for a platform cause, the platform's own words.</param>
/// <param name="Outputs">Values other resources may read through a <c>$ref</c>. A fabric resource reports its platform read side under <c>platform.&lt;platform&gt;</c>.</param>
/// <param name="LastOperation">The address of the last operation on the resource, if any.</param>
public sealed record ResourceStatus(
    long ObservedGeneration,
    ProvisioningState ProvisioningState,
    IReadOnlyList<FabricCondition> Conditions,
    IReadOnlyDictionary<string, JsonElement> Outputs,
    ResourceId? LastOperation);
