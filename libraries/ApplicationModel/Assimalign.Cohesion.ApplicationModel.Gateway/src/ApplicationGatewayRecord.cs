using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What a gateway deployed and recorded: for each resource, the revision deployed, the state written
/// since, the instances registered, and the outputs read.
/// </summary>
/// <remarks>
/// Child gateways are not recorded: each one deploys on its own and joins again after its parent
/// restarts.
/// </remarks>
/// <param name="Gateway">The gateway the record belongs to.</param>
/// <param name="Resources">The resources the gateway deployed, in deployment order.</param>
public sealed record ApplicationGatewayRecord(
    GatewayName Gateway,
    IReadOnlyList<ApplicationGatewayDeploymentRecord> Resources)
{
    /// <summary>
    /// Gets the epoch of the run that holds the record.
    /// </summary>
    /// <remarks>
    /// Each run of a gateway takes its record over with an epoch higher than any run before it took
    /// (<see cref="IApplicationGatewayStore.AcquireAsync"/>), and a store refuses a save whose epoch
    /// is lower than the one it holds. So when two runs of one gateway overlap, such as the old and
    /// the new gateway of a rolling update, the newer run's record wins and the older run stops.
    /// Zero for a record no run has taken over.
    /// </remarks>
    public long Epoch { get; init; }
}
