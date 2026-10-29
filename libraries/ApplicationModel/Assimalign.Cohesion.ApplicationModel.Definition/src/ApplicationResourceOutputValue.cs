namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The value of one resource output, as a gateway passes it to the gateways below it.
/// </summary>
/// <remarks>
/// A gateway deploys a child gateway once the resources the child's part depends on are ready, so it
/// knows their outputs by then. It passes the ones that settings in the child's part use in the
/// child's deployment manifest, and the child fills those settings in from them.
/// </remarks>
/// <param name="Output">The output, of a resource in a gateway above the one that receives it.</param>
/// <param name="Value">The value the resource's infrastructure provider reported.</param>
public sealed record ApplicationResourceOutputValue(ApplicationResourceOutput Output, string Value);
