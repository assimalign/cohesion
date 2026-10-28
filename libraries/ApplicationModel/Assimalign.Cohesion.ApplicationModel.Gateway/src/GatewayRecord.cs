using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What a gateway deployed and recorded: its child gateways and, for each resource, the revision
/// deployed, the state written since, and the instances registered.
/// </summary>
/// <param name="Gateway">The gateway the record belongs to.</param>
/// <param name="Gateways">The child gateways the gateway deployed.</param>
/// <param name="Resources">The resources the gateway deployed, in deployment order.</param>
public sealed record GatewayRecord(
    GatewayName Gateway,
    IReadOnlyList<GatewayName> Gateways,
    IReadOnlyList<GatewayResourceRecord> Resources);
