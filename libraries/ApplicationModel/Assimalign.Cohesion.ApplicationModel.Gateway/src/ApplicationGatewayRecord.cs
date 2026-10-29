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
    IReadOnlyList<ApplicationResourceRecord> Resources);
