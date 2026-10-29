using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One gateway's application as manifest data: the gateway, the parent gateway it joins, and the
/// resources it runs.
/// </summary>
/// <remarks>
/// Each gateway has a manifest of its own. A child gateway names its parent here and sends the
/// manifest when it joins, so the parent learns what the child runs and which of its own outputs the
/// child's settings use.
/// </remarks>
/// <param name="Name">The name of the gateway that runs the resources.</param>
/// <param name="Parent">The gateway this one joins, or <see langword="null"/> for a root gateway.</param>
/// <param name="Resources">The resources the gateway runs, in declaration order.</param>
public sealed record ApplicationManifest(
    GatewayName Name,
    GatewayName? Parent,
    IReadOnlyList<ApplicationResourceManifest> Resources);
