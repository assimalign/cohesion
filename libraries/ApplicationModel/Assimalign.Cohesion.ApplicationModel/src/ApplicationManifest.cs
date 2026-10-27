using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One application set as manifest data: its gateway, the resources the gateway runs, and the
/// manifests of its child gateways. The root set's manifest is the global manifest.
/// </summary>
/// <param name="Name">The name of the gateway that deploys the set.</param>
/// <param name="Resources">The resources the gateway runs, in declaration order.</param>
/// <param name="Gateways">The manifests of the child gateways, in declaration order.</param>
public sealed record ApplicationManifest(
    GatewayName Name,
    IReadOnlyList<ApplicationResourceManifest> Resources,
    IReadOnlyList<ApplicationManifest> Gateways);
