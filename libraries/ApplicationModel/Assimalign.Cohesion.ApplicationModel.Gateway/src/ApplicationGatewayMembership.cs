using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// A parent gateway's answer to a child gateway that joins it: the values of the parent's outputs
/// the child's settings use, and how long the parent keeps the child registered.
/// </summary>
/// <param name="Outputs">
/// The values the parent has recorded, each for an output of one of its resources that a setting of
/// the child uses. An output the parent has not recorded yet, because its resource is not ready, is
/// absent, and the child joins again.
/// </param>
/// <param name="Lease">How long the parent keeps the child registered without another join.</param>
public sealed record ApplicationGatewayMembership(IReadOnlyList<ApplicationGatewayOutputValue> Outputs, TimeSpan Lease);
