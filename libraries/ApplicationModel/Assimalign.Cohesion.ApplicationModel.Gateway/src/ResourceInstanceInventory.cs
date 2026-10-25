using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>Describes platform-owned membership independently of consumer endpoints.</summary>
/// <param name="Incarnation">The persistent deployment identity, changed on delete/recreate.</param>
/// <param name="Revision">The opaque platform membership generation.</param>
/// <param name="DesiredInstances">The current scale target, including HPA changes.</param>
/// <param name="IsComplete">Whether all desired/current membership has been accounted for.</param>
/// <param name="Instances">Individually addressable current instances, including starting processes.</param>
public sealed record ResourceInstanceInventory(
    Guid Incarnation, string Revision, int DesiredInstances, bool IsComplete,
    IReadOnlyList<ResourceManagementTarget> Instances);
