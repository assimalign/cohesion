using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed class ResourceManagementState
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Incarnation { get; set; }
    public long NextRevision { get; set; }
    public Dictionary<string, ResourceManagementSlot> Slots { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<Guid, ResourceStateOperation> Operations { get; init; } = new();
    public Dictionary<string, ResourceManagementRequestReceipt> Requests { get; init; } = new(StringComparer.Ordinal);
}
